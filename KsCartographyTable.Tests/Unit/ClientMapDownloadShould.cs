using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Kaisentlaia.KsCartographyTableMod.GameContent;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace KsCartographyTable.test.Unit;

[NonParallelizable]
public class ClientMapDownloadShould
{
    private string directory;

    [SetUp]
    public void SetUp()
    {
        SQLitePCL.Batteries_V2.Init();
        directory = Directory.CreateTempSubdirectory("KctDownloadTests-").FullName;
    }

    [TearDown]
    public void TearDown()
    {
        if (directory == null || !Directory.Exists(directory)) return;
        string fullPath = Path.GetFullPath(directory);
        string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Assert.That(string.Equals(Path.GetDirectoryName(fullPath), temporaryRoot, comparison), Is.True);
        Directory.Delete(fullPath, true);
    }

    [Test]
    public void CommitDownloadsThroughAnIndependentConnectionAndPreserveExistingTerrain()
    {
        string path = Path.Combine(directory, "map.db");
        var logger = Substitute.For<ILogger>();
        var localPosition = new FastVec2i(100, 200);
        var downloadedPosition = new FastVec2i(300, 400);
        var laterPosition = new FastVec2i(500, 600);
        var localPixels = new[] { 1, 2, 3 };
        var downloadedPixels = new[] { 4, 5, 6 };

        using (var vanilla = new MapDB(logger))
        {
            string error = null;
            Assert.That(vanilla.OpenOrCreate(path, ref error, true, true, false), Is.True, error);
            vanilla.SetMapPieces(new() { [localPosition] = new MapPieceDB { Pixels = localPixels } });

            using (var store = new ClientMapDownloadStore(path, logger))
            {
                store.Store(new() { [downloadedPosition] = new MapPieceDB { Pixels = downloadedPixels } });
                // A separate, already-open connection sees the committed batch.
                Assert.That(vanilla.GetMapPiece(downloadedPosition).Pixels, Is.EqualTo(downloadedPixels));
                Assert.That(vanilla.GetMapPiece(localPosition).Pixels, Is.EqualTo(localPixels));
            }

            // Disposing our store must never close the vanilla map connection.
            vanilla.SetMapPieces(new() { [laterPosition] = new MapPieceDB { Pixels = [7, 8, 9] } });
        }

        using var reopened = new MapDB(logger);
        string reopenError = null;
        Assert.That(reopened.OpenOrCreate(path, ref reopenError, true, true, false), Is.True, reopenError);
        Assert.That(reopened.GetMapPiece(localPosition).Pixels, Is.EqualTo(localPixels));
        Assert.That(reopened.GetMapPiece(downloadedPosition).Pixels, Is.EqualTo(downloadedPixels));
        Assert.That(reopened.GetMapPiece(laterPosition).Pixels, Is.EqualTo(new[] { 7, 8, 9 }));
    }

    [Test]
    public void RefreshOnlyVisibleDownloadedTerrainThroughTheVanillaRenderQueue()
    {
        var visiblePosition = new FastVec2i(100, 200);
        var distantPosition = new FastVec2i(300, 400);
        var pixels = new[] { 1, 2, 3 };
        var queue = new ConcurrentQueue<ReadyMapPiece>();
        var visible = new HashSet<FastVec2i> { visiblePosition };
        var layer = CreateLayer(visible, queue);
        var renderer = new MapDownloadRenderer(layer, Substitute.For<ILogger>());
        var pieces = new Dictionary<FastVec2i, MapPieceDB>
        {
            [visiblePosition] = new MapPieceDB { Pixels = pixels },
            [distantPosition] = new MapPieceDB { Pixels = [4, 5, 6] }
        };

        Assert.That(renderer.QueueVisiblePieces(pieces), Is.EqualTo(1));
        Assert.That(queue.TryDequeue(out var ready), Is.True);
        Assert.That(ready.Cord, Is.EqualTo(visiblePosition));
        Assert.That(ready.Pixels, Is.SameAs(pixels));
        Assert.That(queue, Is.Empty);

        // Closing or moving the map changes the existing set; use the current
        // view, rather than retaining visibility from when the download started.
        visible.Clear();
        Assert.That(renderer.QueueVisiblePieces(pieces), Is.Zero);
        Assert.That(queue, Is.Empty);
    }

    [Test]
    public void TolerateUnavailableMapLayerWithoutLosingThePersistedDownload()
    {
        var logger = Substitute.For<ILogger>();
        var renderer = new MapDownloadRenderer(null, logger);
        Assert.That(renderer.QueueVisiblePieces(new Dictionary<FastVec2i, MapPieceDB>()), Is.Zero);
        logger.Received(1).Warning(Arg.Is<string>(message => message.Contains("Terrain is saved")));
    }

    [Test]
    public async Task ReportActualPersistedIdsInsteadOfHistoricalServerMappings()
    {
        string path = CreateMapInventory(10, 20, 30);
        using var snapshot = new ClientMapIdSnapshot(path);
        await WaitForSnapshot(snapshot);

        Assert.That(snapshot.Error, Is.Null);
        Assert.That(snapshot.Ids, Is.EquivalentTo(new ulong[] { 10, 20, 30 }));
    }

    [Test]
    public async Task RequestAllTerrainWhenTheClientMapFileWasCleared()
    {
        string path = Path.Combine(directory, "cleared-map.db");
        using var snapshot = new ClientMapIdSnapshot(path);
        await WaitForSnapshot(snapshot);

        Assert.That(snapshot.Error, Is.Null);
        Assert.That(snapshot.Ids, Is.Empty);
        Assert.That(File.Exists(path), Is.False, "Reading a missing inventory must not create an empty database.");
    }

    [Test]
    public async Task RejectOversizedInventoryWithoutSendingAPartialList()
    {
        string path = CreateMapInventory(10, 20, 30);
        using var snapshot = new ClientMapIdSnapshot(path, maximumIds: 2);
        await WaitForSnapshot(snapshot);

        Assert.That(snapshot.Error, Is.TypeOf<InvalidOperationException>());
        Assert.That(snapshot.Ids, Is.Null);
    }

    private string CreateMapInventory(params long[] ids)
    {
        string path = Path.Combine(directory, "inventory.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE mappiece (position INTEGER PRIMARY KEY, data BLOB)";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO mappiece(position) VALUES (@id)";
        var parameter = command.Parameters.Add("@id", SqliteType.Integer);
        foreach (long id in ids)
        {
            parameter.Value = id;
            command.ExecuteNonQuery();
        }
        return path;
    }

    private static async Task WaitForSnapshot(ClientMapIdSnapshot snapshot)
    {
        var timer = Stopwatch.StartNew();
        while (!snapshot.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10);
        }
        Assert.That(snapshot.IsCompleted, Is.True, "Background map inventory did not finish.");
    }

    private static ChunkMapLayer CreateLayer(HashSet<FastVec2i> visible, ConcurrentQueue<ReadyMapPiece> queue)
    {
        // Exercise the installed game's actual queue fields without constructing
        // a GUI or starting its world-map worker in a unit test.
        var layer = (ChunkMapLayer)RuntimeHelpers.GetUninitializedObject(typeof(ChunkMapLayer));
        typeof(ChunkMapLayer).GetField("curVisibleChunks", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(layer, visible);
        typeof(ChunkMapLayer).GetField("readyMapPieces", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(layer, queue);
        return layer;
    }
}
