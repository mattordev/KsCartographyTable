using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Kaisentlaia.KsCartographyTableMod.GameContent
{
    // The server's historical delivery records are not proof that the current
    // client still has its map files. Read the actual IDs before a download.
    internal sealed class ClientMapIdSnapshot : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task<ulong[]> readTask;
        private int disposed;

        internal ClientMapIdSnapshot(string databasePath, int maximumIds = 2_000_000)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumIds);
            var token = cancellation.Token;
            readTask = Task.Run(() => ReadIds(databasePath, maximumIds, token), token);
            // A cancelled interaction may never poll Error. Observe failures
            // without touching the game API or scheduling an unload callback.
            _ = readTask.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        internal bool IsCompleted => readTask.IsCompleted;
        internal Exception Error => readTask.IsCanceled
            ? new OperationCanceledException("Client map snapshot was cancelled.")
            : readTask.Exception?.GetBaseException();
        // Ownership transfers to the completed request; callers must not mutate it.
        internal ulong[] Ids => readTask.Status == TaskStatus.RanToCompletion ? readTask.Result : null;

        private static ulong[] ReadIds(string databasePath, int maximumIds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(databasePath)) return [];

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT position FROM mappiece";
            using var reader = command.ExecuteReader();
            var ids = new List<ulong>();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (ids.Count == maximumIds)
                {
                    throw new InvalidOperationException($"The client map exceeds the download inventory limit of {maximumIds} chunks.");
                }
                ids.Add(Convert.ToUInt64(reader.GetValue(0)));
            }
            token.ThrowIfCancellationRequested();
            return ids.ToArray();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            cancellation.Cancel();
            cancellation.Dispose();
            // The worker owns all SQLite resources. Do not wait on the UI thread.
        }
    }
}
