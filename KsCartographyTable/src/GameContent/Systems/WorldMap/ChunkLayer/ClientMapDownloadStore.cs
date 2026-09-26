using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Kaisentlaia.KsCartographyTableMod.GameContent
{
    // This connection belongs exclusively to the download database worker.
    // ChunkMapLayer already uses its own connection on the vanilla map thread.
    internal sealed class ClientMapDownloadStore : IDisposable
    {
        private readonly MapDB database;

        internal ClientMapDownloadStore(string databasePath, ILogger logger)
        {
            database = new ConcurrentMapDB(logger);
            try
            {
                ((ConcurrentMapDB)database).Open(databasePath);
            }
            catch
            {
                database.Dispose();
                throw;
            }
        }

        internal void Store(Dictionary<FastVec2i, MapPieceDB> pieces)
        {
            ArgumentNullException.ThrowIfNull(pieces);
            if (pieces.Count == 0) return;

            // SetMapPieces commits the whole packet before returning. The caller
            // must acknowledge the packet only after this method succeeds.
            database.SetMapPieces(pieces);
        }

        public void Dispose() => database.Dispose();

        private sealed class ConcurrentMapDB(ILogger logger) : MapDB(logger)
        {
            internal void Open(string path)
            {
                // OpenOrCreate's preliminary exclusive file probe rejects the
                // already-open vanilla DB on Windows. SQLite itself arbitrates
                // concurrent connections; retain the game's WAL/Normal settings.
                databaseFileName = path;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                sqliteConn = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
                sqliteConn.Open();
                using var command = sqliteConn.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=Normal;";
                command.ExecuteNonQuery();
                CreateTablesIfNotExists(sqliteConn);
                OnOpened();
            }
        }
    }
}
