using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Kaisentlaia.KsCartographyTableMod.API.Common;
using Kaisentlaia.KsCartographyTableMod.GameContent;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace KsCartographyTable.test.Unit;

[NonParallelizable]
public class WaypointUploadShould
{
    private static readonly DateTime OldUpdate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime();
    private static readonly DateTime LastDownload = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime();
    private string temporaryDirectory;
    private ServerMapDB database;
    private ServerWaypointManager manager;
    private IServerPlayer player;
    private WaypointMapLayer layer;
    private BlockEntityCartographyTable table;
    private ILogger logger;
    private List<string> debugMessages;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), "KctWaypointUploadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        SQLitePCL.Batteries_V2.Init();

        var api = Substitute.For<ICoreServerAPI>();
        var world = Substitute.For<IServerWorldAccessor>();
        api.World.Returns(world);
        ((ICoreAPI)api).World.Returns(world);
        api.Side.Returns(EnumAppSide.Server);
        logger = Substitute.For<ILogger>();
        debugMessages = [];
        logger.When(log => log.Debug(Arg.Any<string>())).Do(call => debugMessages.Add(call.Arg<string>()));
        world.Logger.Returns(logger);
        api.Logger.Returns(logger);
        api.GetOrCreateDataPath(Arg.Any<string>()).Returns(temporaryDirectory);
        // A rooted save identifier keeps the manager's deletion history inside
        // this test directory without changing GamePaths or the user's data.
        world.SavegameIdentifier.Returns(temporaryDirectory);
        Settings.Init(api, "waypoint-upload-tests");
        Settings.Load();

        player = Substitute.For<IServerPlayer>();
        player.PlayerUID.Returns("uploading-player");
        player.PlayerName.Returns("Test player");
        table = new BlockEntityCartographyTable
        {
            Pos = new BlockPos(0, 100, 0),
            Map = new CartographyMap(api)
        };
        table.Map.LastPlayerSyncs[player.PlayerUID] = new DateTimeOffset(LastDownload).ToUnixTimeMilliseconds();
        world.BlockAccessor.GetBlockEntity(table.Pos).Returns(table);

        // This path only reads Waypoints. Avoid starting the game layer's UI,
        // chat commands and event subscriptions while exercising the real manager.
        layer = (WaypointMapLayer)RuntimeHelpers.GetUninitializedObject(typeof(WaypointMapLayer));
        layer.Waypoints = [];
        manager = new ServerWaypointManager(api);
        typeof(ServerWaypointManager).GetField("waypointMapLayer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, layer);

        database = new ServerMapDB(api);
        string error = null;
        Assert.That(database.OpenOrCreate(Path.Combine(temporaryDirectory, "table.db"), ref error, true, true, false), Is.True, error);
    }

    [TearDown]
    public void TearDown()
    {
        database?.Dispose();
        SqliteConnection.ClearAllPools();
        if (temporaryDirectory != null && Directory.Exists(temporaryDirectory))
        {
            string testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KctWaypointUploadTests"))
                + Path.DirectorySeparatorChar;
            string resolvedDirectory = Path.GetFullPath(temporaryDirectory);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            Assert.That(resolvedDirectory.StartsWith(testRoot, comparison), Is.True,
                "Refusing to delete a directory outside the dedicated waypoint test root.");
            Directory.Delete(resolvedDirectory, true);
        }
    }

    [Test]
    public void EmitNoPerformanceLogsWhenVerboseDebugIsDisabled()
    {
        var existing = StoredWaypoint("unchanged");
        database.CreateWaypoints([existing]);
        layer.Waypoints.Add(new CartographyWaypoint(existing));
        Assert.That(Settings.VerboseDebug, Is.False);
        debugMessages.Clear();

        Upload();

        Assert.That(debugMessages.Where(message => message.Contains("[perf]")), Is.Empty);
    }

    [Test]
    public void EmitOneAggregateDiagnosticForAnUnchangedUploadWhenVerboseDebugIsEnabled()
    {
        var first = StoredWaypoint("first");
        var second = StoredWaypoint("second");
        database.CreateWaypoints([first, second]);
        layer.Waypoints.AddRange([
            new CartographyWaypoint(first),
            new CartographyWaypoint(second),
            StoredWaypoint("unrelated", "other-player")
        ]);
        manager.AddDeletedWaypointId(StoredWaypoint("historical-missing-id"), player);
        Settings.VerboseDebug = true;
        debugMessages.Clear();

        var result = Upload();

        var summaries = debugMessages.Where(message => message.Contains("[perf] waypoints.upload-worker ")).ToList();
        Assert.That(summaries, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Synced, Is.False);
            Assert.That(summaries[0], Does.Contain("worldWaypoints=3 playerWaypoints=2 sharedWithPlayer=2"));
            Assert.That(summaries[0], Does.Contain("deletionHistory=1 deletedRows=0 edited=0 rejected=0"));
            Assert.That(summaries[0], Does.Contain("matchQueries=0 create=0 track=0"));
            foreach (string stage in new[]
            {
                "total", "readPlayerWaypoints", "classify", "matchNew",
                "create", "update", "readDeletionHistory", "findDeleted", "delete"
            })
            {
                Assert.That(summaries[0], Does.Contain(stage + "Ms="));
            }
        }
        Settings.VerboseDebug = false;
    }

    [Test]
    public void LeaveUnchangedSharedWaypointsAloneAndIgnoreOtherPlayers()
    {
        var existing = StoredWaypoint("unchanged");
        database.CreateWaypoints([existing]);
        layer.Waypoints.Add(new CartographyWaypoint(existing));
        var otherPlayerCopy = new CartographyWaypoint(existing)
        {
            OwningPlayerUid = "other-player",
            Title = "An unrelated player's edit"
        };
        layer.Waypoints.Add(otherPlayerCopy);

        var result = Upload();
        var actual = database.GetPlayerSharedWaypoints(player).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Synced, Is.False);
            Assert.That(result.Added + result.Edited + result.Rejected + result.Deleted, Is.Zero);
            Assert.That(actual.Title, Is.EqualTo(existing.Title));
            Assert.That(actual.LastUpdated, Is.EqualTo(OldUpdate));
        }
    }

    [TestCase("color")]
    [TestCase("title")]
    [TestCase("icon")]
    [TestCase("pinned")]
    public void UploadEachEditableField(string field)
    {
        var existing = StoredWaypoint("editable");
        database.CreateWaypoints([existing]);
        var changed = new CartographyWaypoint(existing);
        switch (field)
        {
            case "color": changed.Color = 123; break;
            case "title": changed.Title = "New title"; break;
            case "icon": changed.Icon = "circle"; break;
            case "pinned": changed.Pinned = true; break;
        }
        layer.Waypoints.Add(changed);

        var result = Upload();
        var actual = database.GetPlayerSharedWaypoints(player).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Edited, Is.EqualTo(1));
            Assert.That(result.Added + result.Deleted + result.Rejected, Is.Zero);
            Assert.That(result.Synced, Is.True);
            Assert.That(actual.Color, Is.EqualTo(changed.Color));
            Assert.That(actual.Title, Is.EqualTo(changed.Title));
            Assert.That(actual.Icon, Is.EqualTo(changed.Icon));
            Assert.That(actual.Pinned, Is.EqualTo(changed.Pinned));
            Assert.That(actual.LastUpdated, Is.GreaterThan(OldUpdate));
        }
    }

    [Test]
    public void KeepTheFirstDifferingWaypointWhenCurrentGuidsAreDuplicated()
    {
        var existing = StoredWaypoint("duplicate-guid");
        database.CreateWaypoints([existing]);
        layer.Waypoints.AddRange([
            new CartographyWaypoint(existing),
            new CartographyWaypoint(existing) { Title = "First changed copy" },
            new CartographyWaypoint(existing) { Title = "Later changed copy" }
        ]);

        var result = Upload();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Edited, Is.EqualTo(1));
            Assert.That(result.Added, Is.Zero);
            Assert.That(database.GetPlayerSharedWaypoints(player).Single().Title, Is.EqualTo("First changed copy"));
        }
    }

    [Test]
    public void CreateNewRootsAndTrackMatchesWithoutCountingThemAsNewRoots()
    {
        var parent = StoredWaypoint("other-player-root", "other-player");
        database.CreateWaypoints([parent]);
        var matching = new CartographyWaypoint(parent)
        {
            Guid = "local-copy",
            OwningPlayerUid = player.PlayerUID
        };
        var newRoot = StoredWaypoint("new-root");
        newRoot.Title = "A new location";
        layer.Waypoints.AddRange([matching, newRoot]);

        var result = Upload();
        var stored = database.GetPlayerSharedWaypoints(player).ToDictionary(w => w.Guid);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Added, Is.EqualTo(1));
            Assert.That(result.Edited + result.Deleted + result.Rejected, Is.Zero);
            Assert.That(stored, Has.Count.EqualTo(2));
            Assert.That(stored[matching.Guid].ParentGuid, Is.EqualTo(parent.Guid));
            Assert.That(stored[matching.Guid].LastUpdated, Is.EqualTo(parent.LastUpdated));
            Assert.That(stored[newRoot.Guid].ParentGuid, Is.Null);
            Assert.That(database.GetSharedWaypointsCount(), Is.EqualTo(2));
        }
    }

    [Test]
    public void RejectLocalEditsWhenTheTableChangedAfterThePlayersLastDownload()
    {
        var existing = StoredWaypoint("newer-table-copy");
        existing.LastUpdated = LastDownload.AddDays(1);
        database.CreateWaypoints([existing]);
        layer.Waypoints.Add(new CartographyWaypoint(existing) { Title = "Stale local edit" });

        var result = Upload();
        var actual = database.GetPlayerSharedWaypoints(player).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Rejected, Is.EqualTo(1));
            Assert.That(result.Synced, Is.False);
            Assert.That(actual.Title, Is.EqualTo(existing.Title));
            Assert.That(actual.LastUpdated, Is.EqualTo(existing.LastUpdated));
        }
    }

    [Test]
    public void PropagateDeletionHistoryAndTolerateRepeatingOldDeletedIds()
    {
        var parent = StoredWaypoint("deleted-root");
        var child = StoredWaypoint("deleted-child", "other-player");
        child.ParentGuid = parent.Guid;
        var survivor = StoredWaypoint("survivor", "other-player");
        database.CreateWaypoints([parent, child, survivor]);
        manager.AddDeletedWaypointId(parent, player);
        manager.AddDeletedWaypointId(StoredWaypoint("no-longer-in-table"), player);

        var first = Upload();
        var second = Upload();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Deleted, Is.EqualTo(2));
            Assert.That(first.Synced, Is.True);
            Assert.That(second.Deleted, Is.Zero);
            Assert.That(second.Synced, Is.False);
            Assert.That(database.GetWaypointsToDelete([parent.Guid]), Is.Empty);
            Assert.That(database.GetSharedWaypointsCount(), Is.EqualTo(1));
            Assert.That(manager.GetDeletedWaypointsIds(player), Is.EquivalentTo(new[] { parent.Guid, "no-longer-in-table" }));
        }
    }

    [Test]
    public void PrepareWaypointDownloadsWithoutMutatingTheWorldAndPreservePinnedMarkers()
    {
        var root = StoredWaypoint("other-root", "bob");
        root.Pinned = true;
        database.CreateWaypoints([root]);
        var snapshot = manager.Capture(player, table);
        var plan = manager.ReadPlayerWaypoints(snapshot, database);
        Assert.That(layer.Waypoints, Is.Empty, "Worker preparation must not touch live world waypoints.");
        Assert.That(plan.Final, Has.Count.EqualTo(1));
        Assert.That(plan.Final[0].Guid, Is.Not.EqualTo(root.Guid));
        Assert.That(plan.Final[0].OwningPlayerUid, Is.EqualTo(player.PlayerUID));
        Assert.That(plan.Final[0].Pinned, Is.True);
        Assert.That(database.GetPlayerSharedWaypoints(player).Single().ParentGuid, Is.EqualTo(root.Guid));
    }

    [Test]
    public void KeepLiveWaypointEditsMadeWhileTheDatabaseWorkerWasPreparingADownload()
    {
        var stored = StoredWaypoint("edited-while-downloading");
        stored.LastUpdated = LastDownload.AddDays(1);
        database.CreateWaypoints([stored]);
        var live = new CartographyWaypoint(stored) { Title = "Before download" };
        layer.Waypoints.Add(live);
        var snapshot = manager.Capture(player, table);
        var plan = manager.ReadPlayerWaypoints(snapshot, database);
        live.Title = "User edited during the transfer";
        live.Position.X++;
        Assert.That(snapshot.Current[0].Position.X, Is.EqualTo(10), "Snapshots must also copy mutable position objects.");
        var applied = manager.ApplyPlayerWaypoints(player, plan);
        Assert.That(applied.Synced, Is.False);
        Assert.That(live.Title, Is.EqualTo("User edited during the transfer"));
        Assert.That(live.Position.X, Is.EqualTo(11));
    }

    private WaypointSyncResult Upload() => manager.UpdateTableWaypoints(player, table.Pos, database);

    private CartographyWaypoint StoredWaypoint(string guid, string owner = null) => new(new Waypoint
    {
        Guid = guid,
        OwningPlayerUid = owner ?? player.PlayerUID,
        Title = "Original title",
        Icon = "star",
        Color = 1,
        Position = new Vec3d(10, 100, 20),
        Pinned = false
    })
    {
        LastUpdated = OldUpdate
    };
}
