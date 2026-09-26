using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Kaisentlaia.KsCartographyTableMod.GameContent;
using Microsoft.Data.Sqlite;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace KsCartographyTable.test.Unit;

[NonParallelizable]
public class SqliteMapUploadSourceShould
{
    private string directory;
    private string databasePath;
    private readonly List<SqliteMapUploadSource> sources = [];

    [SetUp]
    public void SetUp()
    {
        SQLitePCL.Batteries_V2.Init();
        directory = Path.Combine(Path.GetTempPath(), "KctUploadSourceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "map.db");
        using var connection = OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE mappiece (position INTEGER PRIMARY KEY, data BLOB)";
        command.ExecuteNonQuery();
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var source in sources) source.Dispose();
        await Task.WhenAll(sources.Select(source => source.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        sources.Clear();
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KctUploadSourceTests")) + Path.DirectorySeparatorChar;
        string resolvedDirectory = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Assert.That(resolvedDirectory.StartsWith(root, comparison), Is.True,
            "Refusing to delete outside the dedicated upload-source test root.");
        Directory.Delete(resolvedDirectory, true);
    }

    [Test]
    public async Task ReadOnlyMissingPiecesAcrossPagesAndPreserveCoordinatesAndPixels()
    {
        // ToChunkIndex ORs unsigned X with Y << 27; negative X is not
        // invertible. Real map chunks use absolute, nonnegative world coordinates.
        var positions = new[]
        {
            new FastVec2i(0, 10), new FastVec2i(1, 10), new FastVec2i(2, 10),
            new FastVec2i(3, 10), new FastVec2i(4, 10), new FastVec2i(50000, 10), new FastVec2i(6, 100000)
        };
        for (int i = 0; i < positions.Length; i++)
        {
            // Known rows deliberately contain invalid protobuf. Filtering must
            // happen before loading/deserializing their blobs.
            Insert(positions[i], i is 0 or 3 ? new byte[] { 0x0f } : Pixels(i));
        }
        var before = SHA256.HashData(File.ReadAllBytes(databasePath));
        var known = new HashSet<ulong> { positions[0].ToChunkIndex(), positions[3].ToChunkIndex() };
        var source = Start(known, batchSize: 2, pageSize: 2);
        known.Clear(); // The source must already own an independent snapshot.

        var batches = await Drain(source);
        var actual = batches.SelectMany(batch => batch).ToDictionary(pair => pair.Key, pair => pair.Value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Error, Is.Null);
            Assert.That(source.IsCompleted, Is.True);
            Assert.That(batches.Select(batch => batch.Count), Is.EqualTo(new[] { 2, 2, 1 }));
            Assert.That(source.Progress.ScannedPieces, Is.EqualTo(7));
            Assert.That(source.Progress.PreparedPieces, Is.EqualTo(5));
            Assert.That(source.Progress.DeliveredPieces, Is.EqualTo(5));
            for (int i = 0; i < positions.Length; i++)
            {
                if (i is 0 or 3) Assert.That(actual.ContainsKey(positions[i]), Is.False);
                else Assert.That(actual[positions[i]].Pixels, Is.EqualTo(new[] { i, i + 100, -i - 1 }));
            }
            Assert.That(SHA256.HashData(File.ReadAllBytes(databasePath)), Is.EqualTo(before),
                "An upload must not change the vanilla map database or add schema objects.");
        }
    }

    [Test]
    public async Task BoundPreparationAndReleaseDatabaseReadersBeforeWaitingForTheConsumer()
    {
        for (int i = 0; i < 20; i++) Insert(new FastVec2i(i, 10), Pixels(i));
        var source = Start([], batchSize: 2, queueCapacity: 1, pageSize: 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await source.WaitToReadBatchAsync(timeout.Token);
        // Wait for the second batch to be prepared behind the single queued
        // batch. Further preparation must stop until the consumer makes room.
        while (source.Progress.PreparedPieces < 4)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Progress.QueuedBatches, Is.EqualTo(1));
            Assert.That(source.Progress.PreparedPieces, Is.EqualTo(4));
            Assert.That(source.Progress.ScannedPieces, Is.EqualTo(4));
            Assert.That(source.Completion.IsCompleted, Is.False);
            Assert.That(source.IsCompleted, Is.False);
        }

        // With the fixture's rollback journal, a reader held across the queue
        // wait would prevent this writer from committing (timeout is zero).
        using (var writer = OpenWritable(timeout: 0))
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "UPDATE mappiece SET data=data WHERE position=@position";
            command.Parameters.AddWithValue("@position", (long)new FastVec2i(0, 10).ToChunkIndex());
            Assert.That(command.ExecuteNonQuery(), Is.EqualTo(1));
        }

        source.Dispose();
        await source.Completion.WaitAsync(timeout.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.IsCanceled, Is.True);
            Assert.That(source.TryTakeBatch(out _), Is.False);
            Assert.That(source.Progress.QueuedBatches, Is.Zero);
            Assert.That(source.Error, Is.Null);
        }
        using var exclusive = File.Open(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Test]
    public async Task KeepFinalQueuedBatchAvailableAfterTheProducerFinishes()
    {
        Insert(new FastVec2i(1, 1), Pixels(1));
        var source = Start([], batchSize: 25);

        await source.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(source.IsCompleted, Is.False, "A queued final batch must be consumed before finalization.");
        Assert.That(source.TryTakeBatch(out var batch), Is.True);
        Assert.That(batch, Has.Count.EqualTo(1));
        Assert.That(source.IsCompleted, Is.True);
    }

    [Test]
    public async Task SplitConfiguredChunkCountAtTheSerializedByteBudget()
    {
        byte[][] blobs = Enumerable.Range(0, 3)
            .Select(i => Pixels(i))
            .ToArray();
        for (int i = 0; i < blobs.Length; i++) Insert(new FastVec2i(i, 10), blobs[i]);
        int byteBudget = blobs.Max(blob => blob.Length) + 1;

        var source = Start([], batchSize: 150, maximumBatchBytes: byteBudget);
        var batches = await Drain(source);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Error, Is.Null);
            Assert.That(batches.Select(batch => batch.Count), Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(batches.Sum(batch => batch.Count), Is.EqualTo(3));
        }
    }

    [Test]
    public async Task ReportReadFailuresSeparatelyFromAnEmptySuccessfulUpload()
    {
        string missingPath = Path.Combine(directory, "missing.db");
        var missing = new SqliteMapUploadSource(missingPath, [], 25);
        sources.Add(missing);
        var empty = Start([], batchSize: 25);

        await Task.WhenAll(missing.Completion, empty.Completion).WaitAsync(TimeSpan.FromSeconds(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(missing.Error, Is.TypeOf<SqliteException>());
            Assert.That(missing.IsCompleted, Is.True);
            Assert.That(missing.TryTakeBatch(out _), Is.False);
            Assert.That(File.Exists(missingPath), Is.False, "Read-only mode must not create a missing map.");
            Assert.That(empty.Error, Is.Null);
            Assert.That(empty.IsCompleted, Is.True);
        }
    }

    [Test]
    public async Task SuppressCanceledBatchesWhileAReplacementUploadCompletes()
    {
        for (int i = 0; i < 8; i++) Insert(new FastVec2i(i, 10), Pixels(i));
        var canceled = Start([], batchSize: 1, queueCapacity: 1, pageSize: 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await canceled.WaitToReadBatchAsync(timeout.Token);
        canceled.Dispose();
        canceled.Dispose();
        var replacement = new SqliteMapUploadSource(null, [], 25);
        sources.Add(replacement);

        await Task.WhenAll(canceled.Completion, replacement.Completion).WaitAsync(timeout.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(canceled.TryTakeBatch(out _), Is.False);
            Assert.That(canceled.Progress.QueuedBatches, Is.Zero);
            Assert.That(replacement.IsCanceled, Is.False);
            Assert.That(replacement.IsCompleted, Is.True);
            Assert.That(replacement.Error, Is.Null);
        }
    }

    [Test]
    public async Task ReportMalformedMissingPixelsWithoutPublishingASuccessfulBatch()
    {
        Insert(new FastVec2i(1, 1), [0x0f]);
        var source = Start([], batchSize: 25);

        await source.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Error, Is.Not.Null);
            Assert.That(source.TryTakeBatch(out _), Is.False);
            Assert.That(source.IsCompleted, Is.True);
        }
    }

    private SqliteMapUploadSource Start(IEnumerable<ulong> known, int batchSize, int queueCapacity = 2,
        int pageSize = 256, int maximumBatchBytes = TransferProtocol.MaximumMapDataBytes)
    {
        var source = new SqliteMapUploadSource(databasePath, known, batchSize, queueCapacity, pageSize, maximumBatchBytes);
        sources.Add(source);
        return source;
    }

    private static async Task<List<Dictionary<FastVec2i, MapPieceDB>>> Drain(SqliteMapUploadSource source)
    {
        var result = new List<Dictionary<FastVec2i, MapPieceDB>>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await source.WaitToReadBatchAsync(timeout.Token))
        {
            while (source.TryTakeBatch(out var batch)) result.Add(batch);
        }
        await source.Completion.WaitAsync(timeout.Token);
        return result;
    }

    private SqliteConnection OpenWritable(int timeout = 1)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Pooling = false, DefaultTimeout = timeout
        }.ToString());
        connection.Open();
        return connection;
    }

    private void Insert(FastVec2i position, byte[] bytes)
    {
        using var connection = OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO mappiece(position,data) VALUES (@position,@data)";
        command.Parameters.AddWithValue("@position", (long)position.ToChunkIndex());
        command.Parameters.AddWithValue("@data", bytes);
        command.ExecuteNonQuery();
    }

    private static byte[] Pixels(int value) => SerializerUtil.Serialize(new MapPieceDB { Pixels = [value, value + 100, -value - 1] });
}
