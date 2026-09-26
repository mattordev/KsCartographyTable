using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kaisentlaia.KsCartographyTableMod.API.Common;
using Kaisentlaia.KsCartographyTableMod.GameContent;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Kaisentlaia.KsCartographyTableMod.API.Server
{
    public enum KctCommand
    {
        wipe
    }
    [ProtoContract]
    public class KctCommandPacket
    {
        [ProtoMember(1)]
        public KctCommand Command;

        [ProtoMember(2)]
        public bool DryRun;

        [ProtoMember(3)]
        public bool MapOnly;

        public KctCommandPacket()
        {
        }

        public KctCommandPacket(KctCommand command, bool dryRun, bool mapOnly)
        {
            Command = command;
            DryRun = dryRun;
            MapOnly = mapOnly;
        }
    }
    public class ServerCartographyService : IDisposable
    {
        readonly ICoreServerAPI CoreServerAPI;
        readonly ServerWaypointManager serverWaypointManager;
        readonly SerialDatabaseWorker<DatabaseSet> worker;
        readonly Dictionary<string, Upload> uploads = [];
        readonly Dictionary<string, Download> downloads = [];
        readonly HashSet<string> maintenance = [];
        readonly long tickListener;
        bool disposed;
        static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        sealed class DatabaseSet(string root, ICoreServerAPI api, ILogger logger) : IDisposable
        {
            readonly Dictionary<string, ServerMapDB> connections = [];
            internal ServerMapDB Get(string id)
            {
                if (connections.TryGetValue(id, out var existing)) return existing;
                using var timing = CartographyPerformanceTrace.Start(api, "db.open-worker");
                timing?.Detail($"table={id} cache=miss indexes=create-if-missing");
                if (timing != null) timing.AlwaysLog = true;
                Directory.CreateDirectory(root);
                var db = new ServerMapDB(api, logger);
                string error = null;
                try
                {
                    if (!db.OpenOrCreate(Path.Combine(root, id + ".db"), ref error, true, true, false))
                        throw new IOException(error ?? "Cannot open table database");
                    connections.Add(id, db);
                    return db;
                }
                catch { db.Dispose(); throw; }
            }
            internal void Delete(string id)
            {
                if (connections.Remove(id, out var db)) db.Dispose();
                File.Delete(Path.Combine(root, id + ".db"));
            }
            public void Dispose() { foreach (var db in connections.Values) db.Dispose(); connections.Clear(); }
        }
        sealed class Upload
        {
            internal string Id, Uid, BlockId;
            internal BlockEntityCartographyTable Table;
            internal IServerPlayer Player;
            internal int Expected, Received;
            internal bool Pending;
            internal double LastActivity = Now;
        }
        sealed class Download
        {
            internal string Id, Uid, BlockId;
            internal BlockEntityCartographyTable Table;
            internal IServerPlayer Player;
            internal HashSet<ulong> Known;
            internal long? Cursor;
            internal readonly TransferWindow Window = new();
            internal WaypointSyncResult Waypoints = new(0, 0, 0, 0);
            internal bool Pending, Stop, AppliedWaypoints;
            internal DateTime SyncAt = DateTime.Now;
            internal int Received;
            internal double LastActivity = Now, LastSend;
        }
        sealed record UploadResult(int Received, List<FastVec2i> Ids, WaypointSyncResult Waypoints,
            int WaypointCount, List<Vec3d> Palantir);

        public ServerCartographyService(ICoreServerAPI api)
        {
            CoreServerAPI = api;
            serverWaypointManager = new(api);
            string root = Path.Combine(GamePaths.DataPath, "ModData", api.World.SavegameIdentifier, CartographyTableConstants.MOD_ID);
            var logger = api.Logger;
            worker = new(() => new DatabaseSet(root, api, logger), callback => api.Event.EnqueueMainThreadTask(callback, "cartography-db"));
            RegisterChannels();
            tickListener = api.Event.RegisterGameTickListener(_ => Tick(), 50);
        }
        public WaypointMapLayer WaypointMapLayer => serverWaypointManager.WaypointMapLayer;
        public void RegisterChannels()
        {
            CoreServerAPI.Network.RegisterChannel(CartographyTableConstants.CHANNEL_UPLOAD_TO_SERVER)
                .RegisterMessageType<MapSyncPacket>().SetMessageHandler<MapSyncPacket>(OnMapUploadRequest);
            CoreServerAPI.Network.RegisterChannel(CartographyTableConstants.CHANNEL_DOWNLOAD_TO_CLIENT).RegisterMessageType<MapSyncPacket>();
            CoreServerAPI.Network.RegisterChannel(TransferProtocol.Channel)
                .RegisterMessageType<MapTransferAck>().RegisterMessageType<MapDownloadRequest>()
                .SetMessageHandler<MapTransferAck>(OnDownloadAck).SetMessageHandler<MapDownloadRequest>(OnDownloadRequest);
            CoreServerAPI.Network.RegisterChannel(CartographyTableConstants.CHANNEL_COMMANDS)
                .RegisterMessageType<KctCommandPacket>().SetMessageHandler<KctCommandPacket>(OnKctCommand);
        }
        void OnKctCommand(IServerPlayer player, KctCommandPacket packet)
        { if (packet.Command == KctCommand.wipe) WipeWaypoints(packet.DryRun, player, packet.MapOnly); }

        BlockEntityCartographyTable Validate(IServerPlayer player, string id, BlockPos pos)
        {
            if (disposed || pos == null || !int.TryParse(id, out _) || maintenance.Contains(id)) return null;
            var table = CoreServerAPI.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityCartographyTable;
            if (table?.Block.Id.ToString() != id) return null;
            var p = player.Entity.Pos;
            if (Math.Pow(p.X - pos.X, 2) + Math.Pow(p.Y - pos.Y, 2) + Math.Pow(p.Z - pos.Z, 2) > 100) return null;
            return table;
        }
        bool Live(BlockEntityCartographyTable table) => !disposed &&
            ReferenceEquals(CoreServerAPI.World.BlockAccessor.GetBlockEntity(table.Pos), table);
        void Ack(IServerPlayer player, string id, int seq, string error = null)
        {
            if (disposed) return;
            CoreServerAPI.Network.GetChannel(TransferProtocol.Channel).SendPacket(new MapTransferAck
            { SessionId = id, Sequence = seq, Success = error == null, Error = error }, player);
        }
        void Error(IServerPlayer player, string id, int seq, Exception error)
        {
            CoreServerAPI.Logger.Error("[kscartographytable] Transfer {0} failed: {1}", id, error);
            Ack(player, id, seq, "Cartography transfer failed; completed chunks are saved. Retry and check the logs.");
        }
        void OnMapUploadRequest(IServerPlayer player, MapSyncPacket packet)
        {
            using var mainTrace = CartographyPerformanceTrace.Start(CoreServerAPI, "upload.receive-main");
            if (packet == null || !Guid.TryParse(packet.SessionId, out _) || packet.Pieces == null || packet.Pieces.Count > TransferProtocol.MaximumPieces) return;
            mainTrace?.Detail($"session={packet.SessionId} seq={packet.Sequence} pieces={packet.Pieces.Count} final={packet.IsFinalBatch} outstandingJobs={worker.OutstandingJobs} outstandingBytes={worker.OutstandingBytes}");
            if (mainTrace != null) mainTrace.AlwaysLog = packet.Sequence == 0 || packet.IsFinalBatch;
            var table = Validate(player, packet.BlockId, packet.BlockPos);
            if (table == null) { Ack(player, packet.SessionId, packet.Sequence, "The cartography table is no longer available."); return; }
            if (!uploads.TryGetValue(packet.SessionId, out var state))
            {
                if (packet.Sequence != 0 || uploads.Values.Any(s => s.Uid == player.PlayerUID))
                { Ack(player, packet.SessionId, packet.Sequence, "A previous upload is still finishing."); return; }
                state = new() { Id = packet.SessionId, Uid = player.PlayerUID, Player = player, Table = table, BlockId = packet.BlockId };
                uploads.Add(state.Id, state);
            }
            if (state.Uid != player.PlayerUID || state.Table != table || state.Pending || packet.Sequence != state.Expected) return;
            if (packet.Pieces.Values.Any(p => p?.Pixels == null || p.Pixels.Length != 1024))
            { FailUpload(state, packet.Sequence, new InvalidDataException("Invalid map pixels")); return; }
            var snapshot = packet.IsFinalBatch && packet.IncludeWaypoints && Settings.WaypointUpload
                ? serverWaypointManager.Capture(player, table, true) : null;
            mainTrace?.Mark("validateAndSnapshot");
            bool advanced = table.IsAdvanced;
            int received = state.Received + (advanced ? packet.Pieces.Count : 0);
            string uid = state.Uid, blockId = state.BlockId;
            double queued = Now;
            state.Pending = true; state.LastActivity = Now;
            table.SetWriting(true);
            bool accepted = worker.TryEnqueue(dbSet =>
            {
                using var timing = CartographyPerformanceTrace.Start(CoreServerAPI, "upload.worker");
                timing?.Detail($"session={packet.SessionId} seq={packet.Sequence} pieces={packet.Pieces.Count} queueMs={(Now - queued) * 1000:F1} final={packet.IsFinalBatch}");
                if (timing != null) timing.AlwaysLog = packet.Sequence == 0 || packet.IsFinalBatch;
                var db = dbSet.Get(blockId); timing?.Mark("open");
                if (advanced && packet.Pieces.Count > 0) db.StoreMapPieces(packet.Pieces, uid);
                timing?.Mark("store");
                if (!packet.IsFinalBatch) return new UploadResult(received, null, null, 0, null);
                var waypoints = snapshot == null ? new WaypointSyncResult(0, 0, 0, 0) : serverWaypointManager.StoreTableWaypoints(snapshot, db);
                timing?.Mark("waypoints");
                var ids = advanced ? db.GetAllMapPiecesIds() : null;
                timing?.Mark("readIds");
                return new UploadResult(received, ids, waypoints, db.GetSharedWaypointsCount(), db.GetPalantirWaypointPositions());
            }, result =>
            {
                if (disposed || !uploads.TryGetValue(state.Id, out var current) || current != state) return;
                state.Pending = false; state.Expected++; state.Received = result.Received; state.LastActivity = Now;
                if (!Live(table)) { FailUpload(state, packet.Sequence, new IOException("Table removed during transfer")); return; }
                if (packet.IsFinalBatch)
                {
                    using var publish = CartographyPerformanceTrace.Start(CoreServerAPI, "upload.publish-main");
                    if (publish != null) publish.AlwaysLog = true;
                    publish?.Detail($"session={state.Id} received={result.Received} tableIds={result.Ids?.Count ?? 0}");
                    uploads.Remove(state.Id);
                    if (result.Ids != null) table.UpdateMapExploredAreasIds(result.Ids);
                    ReportUpload(packet, table, player, result);
                }
                Ack(player, state.Id, packet.Sequence);
            }, error => FailUpload(state, packet.Sequence, error), packet.Pieces.Count * 16384L);
            mainTrace?.Mark("enqueue");
            if (!accepted) FailUpload(state, packet.Sequence, new IOException("Database queue is full"));
        }
        void FailUpload(Upload state, int seq, Exception error)
        {
            uploads.Remove(state.Id);
            if (Live(state.Table)) state.Table.SetWriting(false);
            Error(state.Player, state.Id, seq, error);
        }
        void ReportUpload(MapSyncPacket packet, BlockEntityCartographyTable blockEntity, IServerPlayer fromPlayer, UploadResult result)
        {
            using var timing = CartographyPerformanceTrace.Start(CoreServerAPI, "upload.report-main");
            double km2 = result.Received * 0.001024;
            WaypointSyncResult waypointResult = result.Waypoints;
            timing?.Mark("waypoints");

            if (km2 == 0 && blockEntity.IsAdvanced && !packet.Cancelled)
            {
                KsCartographyTableModSystem.ShowChatMessage(CoreServerAPI, fromPlayer, CartographyTableLangCodes.TABLE_MAP_UP_TO_DATE);
            }
            if (!waypointResult.Synced && packet.IncludeWaypoints)
            {
                if (waypointResult.Rejected > 0)
                {
                    KsCartographyTableModSystem.ShowChatMessage(CoreServerAPI, fromPlayer, CartographyTableLangCodes.PLAYER_WAYPOINTS_REJECTED, waypointResult.Rejected.ToString());
                    CoreServerAPI.SendIngameError(fromPlayer, "mapfailure", Lang.Get(CartographyTableLangCodes.FAILURE_UPDATE_FIRST));
                }
                else
                {
                    KsCartographyTableModSystem.ShowChatMessage(CoreServerAPI, fromPlayer, CartographyTableLangCodes.TABLE_WAYPOINTS_UP_TO_DATE);
                }
            }

            if (km2 == 0 && !waypointResult.Synced)
            {
                KsCartographyTableModSystem.DebugLog(CoreServerAPI, "Upload finished without new data");
                // An unchanged upload does not erase existing table content.
                blockEntity.SetWriting(false);
                timing?.Mark("finishState");
                return;
            }

            if (km2 > 0 && blockEntity.IsAdvanced)
            {
                KsCartographyTableModSystem.DebugLog(CoreServerAPI, "Setting written to true");
                blockEntity.SetWritten(true);
                KsCartographyTableModSystem.ShowChatMessage(CoreServerAPI, fromPlayer, CartographyTableLangCodes.TABLE_MAP_UPDATED, $"{km2:F1}");
            }
            if (waypointResult.Synced)
            {
                KsCartographyTableModSystem.DebugLog(CoreServerAPI, "Setting written to true");
                blockEntity.SetWritten(true);
                if (waypointResult.Added > 0)
                {
                    KsCartographyTableModSystem.ShowChatMessage(CoreServerAPI, fromPlayer, CartographyTableLangCodes.TABLE_WAYPOINTS_ADDED, waypointResult.Added.ToString());
                }
                if (waypointResult.Edited > 0)
                {
                    KsCartographyTableModSystem.ShowChatMessage(CoreServerAPI, fromPlayer, CartographyTableLangCodes.TABLE_WAYPOINTS_EDITED, waypointResult.Edited.ToString());
                }
                if (waypointResult.Deleted > 0)
                {
                    KsCartographyTableModSystem.ShowChatMessage(CoreServerAPI, fromPlayer, CartographyTableLangCodes.TABLE_WAYPOINTS_DELETED, waypointResult.Deleted.ToString());
                }
            }
            KsCartographyTableModSystem.DebugLog(CoreServerAPI, "Setting writing to false");
            blockEntity.SetWriting(false);
            timing?.Mark("finishState");
            blockEntity.UpdateMapWaypointCount(result.WaypointCount);
            timing?.Mark("waypointCount");
            blockEntity.SetPalantirWaypointPositions(result.Palantir);
            timing?.Mark("palantir");
        }

        void OnDownloadRequest(IServerPlayer player, MapDownloadRequest request)
        {
            if (request == null || !Guid.TryParse(request.SessionId, out _)) return;
            if (request.Cancelled)
            {
                if (downloads.TryGetValue(request.SessionId, out var old) && old.Uid == player.PlayerUID) old.Stop = true;
                return;
            }
            if (request.KnownIds == null || request.KnownIds.Length > TransferProtocol.MaximumIds) return;
            var table = Validate(player, request.BlockId, request.Position);
            if (table == null) { Ack(player, request.SessionId, -1, "The cartography table is no longer available."); return; }
            if (downloads.Values.Any(s => s.Uid == player.PlayerUID))
            { Ack(player, request.SessionId, -1, "A previous download is still finishing."); return; }
            var state = new Download { Id = request.SessionId, Uid = player.PlayerUID, Player = player, Table = table, BlockId = request.BlockId, Pending = true };
            downloads.Add(state.Id, state);
            var snapshot = request.IncludeWaypoints && Settings.WaypointDownload ? serverWaypointManager.Capture(player, table) : null;
            table.SetWriting(true);
            bool accepted = worker.TryEnqueue(dbSet =>
            {
                using var timing = CartographyPerformanceTrace.Start(CoreServerAPI, "download.prepare-worker");
                if (timing != null) timing.AlwaysLog = true;
                var known = new HashSet<ulong>(request.KnownIds);
                var plan = snapshot == null ? null : serverWaypointManager.ReadPlayerWaypoints(snapshot, dbSet.Get(request.BlockId));
                timing?.Detail($"session={request.SessionId} actualClientIds={known.Count} historicalMapping=ignored");
                return (known, plan);
            }, result =>
            {
                if (!LiveDownload(state)) return;
                state.Known = result.known; state.Pending = false; state.LastActivity = Now;
                if (result.plan != null)
                {
                    state.Waypoints = serverWaypointManager.ApplyPlayerWaypoints(player, result.plan);
                    state.AppliedWaypoints = true;
                }
                SendNextDownload(state);
            }, error => FailDownload(state, error), request.KnownIds.Length * 16L);
            if (!accepted) FailDownload(state, new IOException("Database queue is full"));
        }
        bool LiveDownload(Download s) => !disposed && downloads.TryGetValue(s.Id, out var current) && current == s && Live(s.Table);
        void SendNextDownload(Download state)
        {
            if (!LiveDownload(state) || state.Pending || state.Window.Waiting || state.Window.Complete) return;
            if (!state.Stop && Now - state.LastSend < Math.Max(0.05, Settings.PacketDelay)) return;
            if (state.Stop || !state.Table.IsAdvanced) { SendDownload(state, new([], state.Cursor, true)); return; }
            state.Pending = true;
            string id = state.BlockId; var known = state.Known; var cursor = state.Cursor;
            int batchSize = Math.Clamp(Settings.ChunksPerPacket, 1, TransferProtocol.MaximumPieces);
            double queued = Now;
            if (!worker.TryEnqueue(dbSet =>
            {
                using var timing = CartographyPerformanceTrace.Start(CoreServerAPI, "download.read-worker");
                var batch = dbSet.Get(id).ReadMapBatch(known, cursor, batchSize, TransferProtocol.MaximumMapDataBytes);
                timing?.Detail($"session={state.Id} pieces={batch.Pieces.Count} final={batch.Complete} queueMs={(Now - queued) * 1000:F1}");
                if (timing != null) timing.AlwaysLog = cursor == null || batch.Complete;
                return batch;
            }, batch =>
            {
                if (!LiveDownload(state)) return;
                state.Pending = false;
                SendDownload(state, state.Stop ? new([], state.Cursor, true) : batch);
            }, error => FailDownload(state, error))) FailDownload(state, new IOException("Database queue is full"));
        }
        void SendDownload(Download state, MapReadBatch batch)
        {
            if (!state.Window.TrySend(batch.Complete, out int seq)) return;
            state.Cursor = batch.LastPosition; state.LastSend = state.LastActivity = Now;
            state.Received += batch.Pieces.Count;
            var packet = new MapSyncPacket(batch.Pieces, state.Table.Block, state.Table.Pos, batch.Complete, state.Waypoints, Settings.WaypointDownload)
            { SessionId = state.Id, Sequence = seq, Cancelled = state.Stop };
            CoreServerAPI.Network.GetChannel(CartographyTableConstants.CHANNEL_DOWNLOAD_TO_CLIENT).SendPacket(packet, state.Player);
        }
        void OnDownloadAck(IServerPlayer player, MapTransferAck ack)
        {
            if (ack?.SessionId == null || !downloads.TryGetValue(ack.SessionId, out var state) || state.Uid != player.PlayerUID) return;
            if (!state.Window.Waiting || ack.Sequence != state.Window.NextSequence) return;
            if (!ack.Success) { FailDownload(state, new IOException(ack.Error)); return; }
            state.Window.Acknowledge(ack.Sequence); state.LastActivity = Now;
            if (state.Window.Complete)
            {
                downloads.Remove(state.Id);
                if (Live(state.Table))
                {
                    state.Table.SetWriting(false); if (state.Received > 0 || state.Waypoints.Synced) state.Table.SetWritten(true);
                    if (state.AppliedWaypoints) state.Table.SetPlayerSyncToNow(player, state.SyncAt);
                }
                KsCartographyTableModSystem.DebugLog(CoreServerAPI, $"[perf] download.acknowledged session={state.Id} chunks={state.Received} cancelled={state.Stop}");
            }
            else SendNextDownload(state);
        }
        void FailDownload(Download state, Exception error)
        {
            downloads.Remove(state.Id);
            if (Live(state.Table)) state.Table.SetWriting(false);
            Error(state.Player, state.Id, state.Window.Waiting ? state.Window.NextSequence : -1, error);
        }
        void Tick()
        {
            if (disposed) return;
            foreach (var s in downloads.Values.ToArray())
            {
                if (!Live(s.Table) || Now - s.LastActivity > TransferProtocol.TimeoutSeconds) FailDownload(s, new TimeoutException("Download timed out or table removed"));
                else SendNextDownload(s);
            }
            foreach (var s in uploads.Values.ToArray())
                if (!Live(s.Table) || Now - s.LastActivity > TransferProtocol.TimeoutSeconds) FailUpload(s, s.Expected, new TimeoutException("Upload timed out or table removed"));
        }
        public void WipeTableMap(Block block, IPlayer player, BlockEntityCartographyTable table)
        {
            string id = block.Id.ToString();
            if (!maintenance.Add(id)) return;
            StopTable(id);
            if (!worker.TryEnqueue(dbs => { dbs.Get(id).Wipe(); return true; }, _ =>
            {
                maintenance.Remove(id);
                if (!Live(table)) return;
                table.UpdateMapWaypointCount(0); table.UpdateMapExploredAreasIds([]); table.SetPalantirWaypointPositions([]); table.SetWiping(false);
                KsCartographyTableModSystem.ShowChatMessage(CoreServerAPI, player, CartographyTableLangCodes.TABLE_MAP_WIPED);
            }, error => { maintenance.Remove(id); if (Live(table)) table.SetWiping(false); CoreServerAPI.Logger.Error("[kscartographytable] Wipe failed: {0}", error); }))
            { maintenance.Remove(id); table.SetWiping(false); }
        }
        // A removed table uses the same worker fence as writes, avoiding close/use races.
        public void CleanupMapData(Block block)
        {
            string id = block.Id.ToString(); StopTable(id); maintenance.Add(id);
            if (!worker.TryEnqueue(dbs => { dbs.Delete(id); return true; }, _ => maintenance.Remove(id), error =>
                { maintenance.Remove(id); CoreServerAPI.Logger.Error("[kscartographytable] Cleanup failed: {0}", error); })) maintenance.Remove(id);
        }
        void StopTable(string id)
        {
            foreach (var s in uploads.Values.Where(s => s.BlockId == id).ToArray()) FailUpload(s, s.Expected, new IOException("Table is being cleared"));
            foreach (var s in downloads.Values.Where(s => s.BlockId == id).ToArray()) FailDownload(s, new IOException("Table is being cleared"));
        }
        public void MarkWaypointDeleted(IServerPlayer player, int index)
        {
            var list = WaypointMapLayer.Waypoints.Where(w => w.OwningPlayerUid == player.PlayerUID).ToList();
            if (index >= 0 && index < list.Count) serverWaypointManager.AddDeletedWaypointId(list[index], player);
        }
        public TextCommandResult WipeWaypoints(bool dryRun, IServerPlayer player, bool mapOnly) => serverWaypointManager.ClearAllWaypoints(dryRun, player, mapOnly);
        internal bool HasCartographyDownloadSession(IPlayer player, Block block) => downloads.Values.Any(s => s.Uid == player.PlayerUID && s.BlockId == block?.Id.ToString());
        internal bool StartCartographyDownloadSession(CartographyAction action, IWorldAccessor world, IPlayer player, Block block, BlockPos pos, BlockEntityCartographyTable table) => true;
        internal bool ContinueCartographyDownloadSession(IPlayer player, float seconds, Block block, BlockEntityCartographyTable table) => true;
        // Client cancellation is ordered with its inventory request on the same channel.
        internal void EndCartographyDownloadSession(IPlayer player, Block block, BlockEntityCartographyTable table) { table.ClearRecentInteraction(player); }
        internal void CleanupPlayerSessions(IServerPlayer player)
        {
            foreach (var s in uploads.Values.Where(s => s.Uid == player.PlayerUID).ToArray()) { uploads.Remove(s.Id); if (Live(s.Table)) s.Table.SetWriting(false); }
            foreach (var s in downloads.Values.Where(s => s.Uid == player.PlayerUID).ToArray()) { downloads.Remove(s.Id); if (Live(s.Table)) s.Table.SetWriting(false); }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; CoreServerAPI.Event.UnregisterGameTickListener(tickListener);
            uploads.Clear(); downloads.Clear();
            // Accepted writes finish and the worker closes its own connections; never join the game thread.
            worker.Complete();
        }
    }
}
