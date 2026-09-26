using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Kaisentlaia.KsCartographyTableMod.API.Common;
using Kaisentlaia.KsCartographyTableMod.GameContent;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace KsCartographyTable.test.Unit;

[NonParallelizable]
public class ServerMapDBShould
{
    private string directory;
    private string databasePath;
    private ICoreAPI api;
    private InspectableMapDB database;

    [SetUp]
    public void SetUp()
    {
        SQLitePCL.Batteries_V2.Init();
        directory = Path.Combine(Path.GetTempPath(), "kct-db-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "table.db");
        api = Substitute.For<ICoreAPI>();
        api.Side.Returns(EnumAppSide.Server);
        api.World.Returns(Substitute.For<IWorldAccessor>());
        api.World.Logger.Returns(Substitute.For<ILogger>());
        api.Logger.Returns(Substitute.For<ILogger>());
        api.GetOrCreateDataPath(Arg.Any<string>()).Returns(directory);
        Settings.Init(api, "kscartographytable");
        Settings.Load();
    }

    [TearDown]
    public void TearDown()
    {
        database?.Dispose();
        database = null;
        if (directory != null && Path.GetDirectoryName(Path.GetFullPath(directory)) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
        {
            Directory.Delete(directory, true);
        }
    }

    private void Open()
    {
        database = new InspectableMapDB(api);
        string error = null;
        Assert.That(database.OpenOrCreate(databasePath, ref error, true, true, false), Is.True, error);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void AddIndexesToAnExistingDatabaseWithoutLosingRowsAndReopenIdempotently()
    {
        // Reproduce the 2.0.4 schema without secondary waypoint indexes.
        using (var oldDatabase = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            oldDatabase.Open();
            using var command = oldDatabase.CreateCommand();
            command.CommandText = "CREATE TABLE sharedwaypoints (guid text NOT NULL, parentGuid text, owningPlayerUid text NOT NULL, position text NOT NULL, title text NOT NULL, icon text NOT NULL, color integer NOT NULL, pinned integer NOT NULL, deleted integer NOT NULL, lastUpdated integer NOT NULL, PRIMARY KEY (guid)); " +
                "INSERT INTO sharedwaypoints VALUES ('original', NULL, 'alice', '12,100,34', 'Old marker', 'circle', 123, 0, 0, 1000)";
            command.ExecuteNonQuery();
        }

        for (int open = 0; open < 2; open++)
        {
            Open();
            Assert.That(database.WaypointIndexCount(), Is.EqualTo(3));
            var waypoints = database.GetPlayerSharedWaypoints(new FakePlayer("alice"));
            Assert.That(waypoints.Select(w => w.Guid), Is.EqualTo(new[] { "original" }));
            Assert.That(waypoints[0].Title, Is.EqualTo("Old marker"));
            database.Dispose();
            database = null;
        }
    }

    [TestCase("getMatchingWaypointCmd", "idx_sharedwaypoints_match")]
    [TestCase("getWaypointsToDeleteCmd", "idx_sharedwaypoints_parent")]
    [TestCase("updateWaypointsCmd", "idx_sharedwaypoints_parent")]
    [TestCase("setDeletedWaypointsCmd", "idx_sharedwaypoints_parent")]
    [TestCase("getPlayerWaypointsCmd", "idx_sharedwaypoints_owner")]
    [TestCase("getUpdatedWaypointsForPlayerCmd", "idx_sharedwaypoints_owner")]
    [TestCase("getDeletedWaypointsForPlayerCmd", "idx_sharedwaypoints_owner")]
    public void SeekThroughIndexesInsteadOfScanningAllWaypoints(string commandField, string expectedIndex)
    {
        Open();
        // Explain the actual prepared production command, including a deletion
        // GUID that no longer exists. Such misses must remain indexed on every upload.
        var plan = database.ExplainPreparedCommand(commandField);
        Assert.That(plan, Does.Contain(expectedIndex));
        Assert.That(plan, Does.Not.Contain("SCAN sharedwaypoints"), plan);
    }

    [Test]
    public void MatchOnlyAnotherPlayersLiveRootWithTheSameFields()
    {
        Open();
        var candidate = Waypoint("candidate", "alice");
        var own = Waypoint("own", "alice");
        var child = Waypoint("child", "bob", "parent-not-present");
        var deleted = Waypoint("deleted", "bob");
        var moved = Waypoint("moved", "bob");
        moved.Position.X++;
        var renamed = Waypoint("renamed", "bob");
        renamed.Title += " changed";
        var otherIcon = Waypoint("other-icon", "bob");
        otherIcon.Icon = "star";
        var pinned = Waypoint("pinned", "bob");
        pinned.Pinned = true;
        database.CreateWaypoints([own, child, deleted, moved, renamed, otherIcon, pinned]);
        database.DeleteWaypoints([deleted]);
        Assert.That(database.GetMatchingWaypoint(candidate), Is.Null);

        var matching = Waypoint("matching", "bob");
        matching.Color = 456; // Matching has never required identical color.
        database.CreateWaypoints([matching]);
        Assert.That(database.GetMatchingWaypoint(candidate)?.Guid, Is.EqualTo(matching.Guid));
    }

    [Test]
    public void PropagateChildEditsAndDeletionsToTheRootAndItsChildrenOnly()
    {
        Open();
        var root = Waypoint("root", "alice");
        var child = Waypoint("child", "bob", root.Guid);
        var sibling = Waypoint("sibling", "carol", root.Guid);
        var unrelated = Waypoint("unrelated", "dave");
        database.CreateWaypoints([root, child, sibling, unrelated]);

        child.Title = "Updated via child";
        child.Color = 987;
        child.Pinned = true;
        database.UpdateWaypoints([child]);
        foreach (string owner in new[] { "alice", "bob", "carol" })
        {
            var updated = database.GetPlayerSharedWaypoints(new FakePlayer(owner)).Single();
            Assert.That(updated.Title, Is.EqualTo(child.Title));
            Assert.That(updated.Color, Is.EqualTo(child.Color));
            Assert.That(updated.Pinned, Is.True);
        }
        Assert.That(database.GetPlayerSharedWaypoints(new FakePlayer("dave")).Single().Title, Is.EqualTo(unrelated.Title));

        var deletions = database.GetWaypointsToDelete([root.Guid, "already-deleted-historical-guid"]);
        Assert.That(deletions.Select(w => w.Guid), Is.EquivalentTo(new[] { root.Guid, child.Guid, sibling.Guid }));
        database.DeleteWaypoints([child]);
        Assert.That(database.GetWaypointsToDelete([root.Guid]), Is.Empty);
        Assert.That(database.GetPlayerSharedWaypoints(new FakePlayer("bob")), Is.Empty);
        Assert.That(database.GetPlayerSharedWaypoints(new FakePlayer("dave")), Has.Count.EqualTo(1));
    }

    [Test]
    public void PreserveMapPiecesAndPlayerMappingsAcrossReopenAndWipe()
    {
        Open();
        var player = new FakePlayer("alice");
        var pieces = new Dictionary<FastVec2i, MapPieceDB>
        {
            // Vintage Story map pieces use absolute world chunk coordinates.
            [new FastVec2i(10, 20)] = new MapPieceDB { Pixels = [1, 2, 3] },
            [new FastVec2i(30, 40)] = new MapPieceDB { Pixels = [4, 5, 6] }
        };
        database.SetMapPieces(pieces);
        database.SetMapPiecesForPlayer(pieces, player);
        database.CreateWaypoints([Waypoint("root", "alice")]);
        database.Dispose();
        Open();

        Assert.That(database.GetAllMapPiecesIds(), Is.EquivalentTo(pieces.Keys));
        Assert.That(database.GetNewMapPiecesForPlayer(player), Is.Empty);
        var loaded = database.GetAllMapPieces();
        foreach (var piece in pieces)
        {
            Assert.That(loaded[piece.Key].Pixels, Is.EqualTo(piece.Value.Pixels));
        }
        database.Wipe();
        Assert.That(database.GetAllMapPiecesIds(), Is.Empty);
        Assert.That(database.GetPlayerSharedWaypoints(player), Is.Empty);
        Assert.That(database.WaypointIndexCount(), Is.EqualTo(3));
        database.SetMapPieces(pieces);
        Assert.That(database.GetNewMapPiecesForPlayer(player), Has.Count.EqualTo(2));
    }

    [Test]
    public void RedownloadForTheSamePlayerAfterTheirClientMapWasCleared()
    {
        Open();
        var pieces = new Dictionary<FastVec2i, MapPieceDB>
        {
            [new(10, 20)] = new() { Pixels = [1, 2, 3] },
            [new(11, 20)] = new() { Pixels = [4, 5, 6] }
        };
        database.StoreMapPieces(pieces, "alice");
        Assert.That(database.GetNewMapPiecesForPlayer("alice"), Is.Empty,
            "Reproduces the old false 'already transcribed' result for the uploader.");
        var restored = database.ReadMapBatch([], null);
        Assert.That(restored.Pieces.Keys, Is.EquivalentTo(pieces.Keys));
        Assert.That(restored.Complete, Is.True);
        foreach (var piece in pieces) Assert.That(restored.Pieces[piece.Key].Pixels, Is.EqualTo(piece.Value.Pixels));
        var known = pieces.Keys.Select(k => k.ToChunkIndex()).ToHashSet();
        Assert.That(database.ReadMapBatch(known, null).Pieces, Is.Empty,
            "A repeat download skips terrain actually present on this client.");
    }

    [Test]
    public void PageDownloadsAcrossExcludedRowsWithoutDuplicatesOrUnboundedBatches()
    {
        Open();
        var pieces = Enumerable.Range(0, 600).ToDictionary(i => new FastVec2i(i, 10), i => new MapPieceDB { Pixels = [i] });
        database.StoreMapPieces(pieces, "alice");
        var known = pieces.Keys.Where(k => k.X < 270 || k.X > 570).Select(k => k.ToChunkIndex()).ToHashSet();
        var found = new Dictionary<FastVec2i, MapPieceDB>();
        long? cursor = null;
        bool complete = false;
        for (int pages = 0; pages < 30 && !complete; pages++)
        {
            var batch = database.ReadMapBatch(known, cursor, 25);
            Assert.That(batch.Pieces.Count, Is.LessThanOrEqualTo(25));
            if (cursor != null) Assert.That(batch.LastPosition, Is.GreaterThan(cursor));
            foreach (var p in batch.Pieces) found.Add(p.Key, p.Value);
            cursor = batch.LastPosition; complete = batch.Complete;
        }
        Assert.That(complete, Is.True);
        Assert.That(found.Keys, Is.EquivalentTo(pieces.Keys.Where(k => !known.Contains(k.ToChunkIndex()))));
        foreach (var p in found) Assert.That(p.Value.Pixels, Is.EqualTo(new[] { p.Key.X }));
    }

    [Test]
    public void RollBackPixelsAndMappingsTogetherWhenAMappingWriteFails()
    {
        Open();
        database.Execute("CREATE TRIGGER fail_mapping BEFORE INSERT ON playerchunkmapping WHEN NEW.playerId='fail' BEGIN SELECT RAISE(ABORT,'injected write failure'); END");
        var pieces = new Dictionary<FastVec2i, MapPieceDB> { [new(10, 20)] = new() { Pixels = [1, 2, 3] } };
        Assert.Throws<SqliteException>(() => database.StoreMapPieces(pieces, "fail"));
        Assert.That(database.GetAllMapPiecesIds(), Is.Empty, "A failed mapping must not leave a partially committed packet.");
        database.StoreMapPieces(pieces, "alice");
        database.StoreMapPieces(new() { [new(11, 20)] = new() { Pixels = [4, 5, 6] } }, "alice");
        Assert.That(database.GetAllMapPiecesIds(), Has.Count.EqualTo(2));
        Assert.That(database.GetNewMapPiecesForPlayer("alice"), Is.Empty);
    }

    private static CartographyWaypoint Waypoint(string guid, string owner, string parent = null)
    {
        return new CartographyWaypoint(new Waypoint
        {
            Guid = guid,
            OwningPlayerUid = owner,
            Position = new Vec3d(12, 100, 34),
            Title = "Marker 'with quotes'",
            Icon = "circle",
            Color = 123
        })
        { ParentGuid = parent };
    }

    private sealed class InspectableMapDB(ICoreAPI api) : ServerMapDB(api)
    {
        public void Execute(string sql)
        {
            using var command = sqliteConn.CreateCommand();
            command.CommandText = sql; command.ExecuteNonQuery();
        }

        public int WaypointIndexCount()
        {
            using var command = sqliteConn.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'idx_sharedwaypoints_%'";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public string ExplainPreparedCommand(string commandField)
        {
            var field = typeof(ServerMapDB).GetField(commandField, BindingFlags.Instance | BindingFlags.NonPublic);
            var prepared = (SqliteCommand)field.GetValue(this);
            using var command = sqliteConn.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + prepared.CommandText;
            foreach (SqliteParameter parameter in prepared.Parameters)
            {
                command.Parameters.AddWithValue(parameter.ParameterName, parameter.SqliteType == SqliteType.Integer ? 0 : "missing-waypoint");
            }
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            return string.Join("\n", plan);
        }
    }
}
