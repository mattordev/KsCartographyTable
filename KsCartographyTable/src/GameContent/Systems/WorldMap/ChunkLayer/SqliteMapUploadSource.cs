using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Kaisentlaia.KsCartographyTableMod.GameContent
{
    internal interface IMapUploadSource : IDisposable
    {
        bool TryTakeBatch(out Dictionary<FastVec2i, MapPieceDB> batch);
        // Completion includes draining the queue. A temporarily empty queue is
        // not the end of an upload while the producer is still reading.
        bool IsCompleted { get; }
        bool IsCanceled { get; }
        Exception Error { get; }
        MapUploadProgress Progress { get; }
        Task Completion { get; }
    }

    internal readonly record struct MapUploadProgress(
        int ScannedPieces, int PreparedPieces, int DeliveredPieces, int QueuedBatches,
        long ReadBytes, double ElapsedMs, double OpenMs, double ReadMs,
        double DeserializeMs, double QueueWaitMs);

    // This object owns its connection and the captured table-ID snapshot. Its
    // worker never accesses the world, the vanilla map layer, or network APIs.
    internal sealed class SqliteMapUploadSource : IMapUploadSource
    {
        private readonly Channel<Dictionary<FastVec2i, MapPieceDB>> batches;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private int disposed;
        private int producerCompleted;
        private Exception error;
        private int scannedPieces;
        private int preparedPieces;
        private int deliveredPieces;
        private long readBytes;
        private long openTicks;
        private long readTicks;
        private long deserializeTicks;
        private long queueWaitTicks;

        public Task Completion { get; }
        public bool IsCanceled => Volatile.Read(ref disposed) != 0;
        public bool IsCompleted => Volatile.Read(ref producerCompleted) != 0 && !batches.Reader.TryPeek(out _);
        public Exception Error => Volatile.Read(ref error);

        public MapUploadProgress Progress => new(
            Volatile.Read(ref scannedPieces), Volatile.Read(ref preparedPieces),
            Volatile.Read(ref deliveredPieces), batches.Reader.Count,
            Interlocked.Read(ref readBytes), elapsed.Elapsed.TotalMilliseconds,
            Milliseconds(Interlocked.Read(ref openTicks)), Milliseconds(Interlocked.Read(ref readTicks)),
            Milliseconds(Interlocked.Read(ref deserializeTicks)), Milliseconds(Interlocked.Read(ref queueWaitTicks)));

        internal SqliteMapUploadSource(string databasePath, IEnumerable<ulong> tableIds,
            int batchSize, int queueCapacity = 2, int pageSize = 256,
            int maximumBatchBytes = TransferProtocol.MaximumMapDataBytes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumBatchBytes, 1);
            var knownIds = new HashSet<ulong>(tableIds);
            batches = Channel.CreateBounded<Dictionary<FastVec2i, MapPieceDB>>(new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                // Dispose can drain the queue while the worker exits.
                SingleReader = false,
                AllowSynchronousContinuations = false
            });
            Completion = Task.Run(() => Produce(databasePath, knownIds, batchSize, pageSize, maximumBatchBytes));
        }

        public bool TryTakeBatch(out Dictionary<FastVec2i, MapPieceDB> batch)
        {
            batch = null;
            if (IsCanceled || !batches.Reader.TryRead(out batch)) return false;
            Interlocked.Add(ref deliveredPieces, batch.Count);
            return true;
        }

        internal ValueTask<bool> WaitToReadBatchAsync(CancellationToken token) => batches.Reader.WaitToReadAsync(token);

        private async Task Produce(string databasePath, HashSet<ulong> knownIds, int batchSize,
            int pageSize, int maximumBatchBytes)
        {
            CancellationToken token = cancellation.Token;
            try
            {
                // A basic table only uploads waypoints, so it has no map file to read.
                if (databasePath == null) return;
                token.ThrowIfCancellationRequested();
                long started = Stopwatch.GetTimestamp();
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false,
                    DefaultTimeout = 1
                }.ToString());
                connection.Open();
                Interlocked.Add(ref openTicks, Stopwatch.GetTimestamp() - started);

                using var pageCommand = connection.CreateCommand();
                pageCommand.CommandText = "SELECT position FROM mappiece WHERE position > @last ORDER BY position LIMIT @limit";
                pageCommand.Parameters.Add("@last", SqliteType.Integer);
                pageCommand.Parameters.AddWithValue("@limit", pageSize);
                using var pieceCommand = connection.CreateCommand();
                pieceCommand.CommandText = "SELECT data FROM mappiece WHERE position=@position";
                pieceCommand.Parameters.Add("@position", SqliteType.Integer);

                long lastPosition = long.MinValue;
                var positions = new List<long>(pageSize);
                var batch = new Dictionary<FastVec2i, MapPieceDB>(batchSize);
                long batchBytes = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    positions.Clear();
                    pageCommand.Parameters["@last"].Value = lastPosition;
                    started = Stopwatch.GetTimestamp();
                    using (var reader = pageCommand.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            token.ThrowIfCancellationRequested();
                            positions.Add(reader.GetInt64(0));
                        }
                    }
                    Interlocked.Add(ref readTicks, Stopwatch.GetTimestamp() - started);
                    if (positions.Count == 0) break;
                    lastPosition = positions[^1];

                    foreach (long position in positions)
                    {
                        token.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref scannedPieces);
                        ulong chunkId = unchecked((ulong)position);
                        if (knownIds.Contains(chunkId)) continue;
                        pieceCommand.Parameters["@position"].Value = position;
                        started = Stopwatch.GetTimestamp();
                        // ExecuteScalar disposes its reader before any wait for queue
                        // capacity. No read transaction spans the network transfer.
                        var bytes = pieceCommand.ExecuteScalar() as byte[];
                        Interlocked.Add(ref readTicks, Stopwatch.GetTimestamp() - started);
                        if (bytes == null) continue;
                        Interlocked.Add(ref readBytes, bytes.Length);
                        token.ThrowIfCancellationRequested();
                        if (batch.Count > 0 && batchBytes + bytes.Length > maximumBatchBytes)
                        {
                            await Enqueue(batch, token).ConfigureAwait(false);
                            batch = new Dictionary<FastVec2i, MapPieceDB>(batchSize);
                            batchBytes = 0;
                        }
                        started = Stopwatch.GetTimestamp();
                        var piece = SerializerUtil.Deserialize<MapPieceDB>(bytes);
                        Interlocked.Add(ref deserializeTicks, Stopwatch.GetTimestamp() - started);
                        batch.Add(DecodeChunkId(chunkId), piece);
                        batchBytes += bytes.Length;
                        Interlocked.Increment(ref preparedPieces);
                        if (batch.Count == batchSize)
                        {
                            await Enqueue(batch, token).ConfigureAwait(false);
                            batch = new Dictionary<FastVec2i, MapPieceDB>(batchSize);
                            batchBytes = 0;
                        }
                    }
                }
                if (batch.Count > 0) await Enqueue(batch, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Releasing the interaction or leaving the world is normal cancellation.
            }
            catch (Exception exception)
            {
                Volatile.Write(ref error, exception);
            }
            finally
            {
                knownIds.Clear();
                batches.Writer.TryComplete();
                if (IsCanceled) while (batches.Reader.TryRead(out _)) { }
                elapsed.Stop();
                Volatile.Write(ref producerCompleted, 1);
            }
        }

        private async Task Enqueue(Dictionary<FastVec2i, MapPieceDB> batch, CancellationToken token)
        {
            long started = Stopwatch.GetTimestamp();
            await batches.Writer.WriteAsync(batch, token).ConfigureAwait(false);
            Interlocked.Add(ref queueWaitTicks, Stopwatch.GetTimestamp() - started);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            cancellation.Cancel();
            while (batches.Reader.TryRead(out _)) { }
            // No caller waits for I/O or a blocked producer on the game thread.
            _ = Completion.ContinueWith(_ => cancellation.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

        private static FastVec2i DecodeChunkId(ulong chunkId)
        {
            int x = (int)(chunkId & 0x7FFFFFF);
            int z = (int)((chunkId >> 27) & 0x7FFFFFF);
            if ((x & 0x4000000) != 0) x |= unchecked((int)0xF8000000);
            if ((z & 0x4000000) != 0) z |= unchecked((int)0xF8000000);
            return new FastVec2i(x, z);
        }
    }
}
