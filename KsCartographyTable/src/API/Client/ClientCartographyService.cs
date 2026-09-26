using System;
using System.Collections.Generic;
using System.Linq;
using Kaisentlaia.KsCartographyTableMod.API.Common;
using Kaisentlaia.KsCartographyTableMod.API.Server;
using Kaisentlaia.KsCartographyTableMod.GameContent;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Kaisentlaia.KsCartographyTableMod.API.Client
{
    [ProtoContract]
    public class CoordsPacket
    {
        [ProtoMember(1)]
        public double X { get; set; }

        [ProtoMember(2)]
        public double Y { get; set; }

        [ProtoMember(3)]
        public double Z { get; set; }

        public CoordsPacket() { }

        public CoordsPacket(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    [ProtoContract]
    public class PalantirTravelPacket
    {
        [ProtoMember(1)]
        public List<CoordsPacket> Waypoints { get; set; } = new();

        [ProtoMember(2)]
        public CoordsPacket PlayerStartingPos { get; set; } = new();

        public PalantirTravelPacket() { }
        public PalantirTravelPacket(List<CoordsPacket> waypoints, CoordsPacket playerStartingPos)
        {
            Waypoints = waypoints;
            PlayerStartingPos = playerStartingPos;
        }
    }

    public class ClientCartographyService : IDisposable
    {
        readonly ICoreClientAPI CoreClientAPI;
        readonly PlayerMapManager playerMapManager;
        SerialDatabaseWorker<ClientMapDownloadStore> downloadWorker;
        MapDownloadRenderer renderer;
        readonly long tickListener;
        bool disposed;
        Upload upload;
        Download download;
        static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        sealed class Upload
        {
            internal readonly string Id = Guid.NewGuid().ToString();
            internal readonly TransferWindow Window = new();
            internal BlockEntityCartographyTable Table;
            internal IMapUploadSource Source;
            internal bool Stop, Released, IncludeWaypoints;
            internal double LastSend, LastActivity = Now, LastLog;
            internal int Sent;
        }
        sealed class Download
        {
            internal readonly string Id = Guid.NewGuid().ToString();
            internal BlockEntityCartographyTable Table;
            internal ClientMapIdSnapshot Snapshot;
            internal bool Requested, Pending, Stop, IncludeWaypoints;
            internal int Expected, Received;
            internal double Started = Now, LastActivity = Now;
        }
        public ChunkMapLayer ChunkMapLayer => playerMapManager.ChunkMapLayer;
        string MapPath => System.IO.Path.Combine(Vintagestory.API.Config.GamePaths.DataPath, "Maps", CoreClientAPI.World.SavegameIdentifier + ".db");
        public ClientCartographyService(ICoreClientAPI api)
        {
            CoreClientAPI = api; playerMapManager = new(api);
            RegisterChannels();
            tickListener = api.Event.RegisterGameTickListener(_ => Tick(), 50);
        }
        public void RegisterChannels()
        {
            CoreClientAPI.Network.RegisterChannel(CartographyTableConstants.CHANNEL_UPLOAD_TO_SERVER).RegisterMessageType<MapSyncPacket>();
            CoreClientAPI.Network.RegisterChannel(CartographyTableConstants.CHANNEL_DOWNLOAD_TO_CLIENT)
                .RegisterMessageType<MapSyncPacket>().SetMessageHandler<MapSyncPacket>(OnMapDownloadRequest);
            CoreClientAPI.Network.RegisterChannel(TransferProtocol.Channel)
                .RegisterMessageType<MapTransferAck>().RegisterMessageType<MapDownloadRequest>()
                .SetMessageHandler<MapTransferAck>(OnAck);
            if (KsCartographyTableModSystem.ModCompatibilityManager.IsPalantirEnabled)
                CoreClientAPI.Network.RegisterChannel(CartographyTableConstants.CHANNEL_SEND_TO_PALANTIR).RegisterMessageType<PalantirTravelPacket>();
            CoreClientAPI.Network.RegisterChannel(CartographyTableConstants.CHANNEL_COMMANDS).RegisterMessageType<KctCommandPacket>();
        }
        public void WipeWaypoints(bool dryRun, bool mapOnly) => CoreClientAPI.Network.GetChannel(CartographyTableConstants.CHANNEL_COMMANDS).SendPacket(new KctCommandPacket(KctCommand.wipe, dryRun, mapOnly));
        void Notice(string text)
        {
            CoreClientAPI.Logger.Warning("[kscartographytable] {0}", text);
            CoreClientAPI.ShowChatMessage(text);
        }
        bool Live(BlockEntityCartographyTable table) => !disposed && ReferenceEquals(CoreClientAPI.World.BlockAccessor.GetBlockEntity(table.Pos), table);
        void Tick()
        {
            if (disposed) return;
            if (upload is { } u)
            {
                if (!Live(u.Table) || (!u.Window.Complete && Now - u.LastActivity > TransferProtocol.TimeoutSeconds)) FailUpload("Upload timed out or the table was removed. Completed chunks are saved.");
                else PumpUpload(u);
            }
            if (download is { } d)
            {
                if (!Live(d.Table) || Now - d.LastActivity > TransferProtocol.TimeoutSeconds) { CancelDownload(d); FailDownload("Download timed out or the table was removed."); }
                else if (!d.Requested && d.Snapshot.IsCompleted)
                {
                    if (d.Snapshot.Error is { } error) { FailDownload("Cannot read the local map: " + error.Message); return; }
                    var ids = d.Snapshot.Ids;
                    d.Requested = true; d.LastActivity = Now;
                    CoreClientAPI.Network.GetChannel(TransferProtocol.Channel).SendPacket(new MapDownloadRequest
                    { SessionId = d.Id, BlockId = d.Table.Block.Id.ToString(), Position = d.Table.Pos.Copy(), KnownIds = ids, IncludeWaypoints = d.IncludeWaypoints });
                    KsCartographyTableModSystem.DebugLog(CoreClientAPI, $"[perf] download.inventory session={d.Id} actualClientIds={ids.Length} elapsedMs={(Now - d.Started) * 1000:F1} payloadIdBytes={ids.Length * 8}");
                    d.Snapshot.Dispose(); d.Snapshot = null;
                }
            }
        }
        void PumpUpload(Upload u)
        {
            if (u.Window.Waiting || u.Window.Complete) return;
            if (!u.Stop && u.Source.Error is { } error) { AbortRead(u, error); return; }
            if (!u.Stop && Now - u.LastSend < Math.Max(0.05, Settings.PacketDelay)) return;
            Dictionary<FastVec2i, MapPieceDB> pieces = [];
            bool final = u.Stop;
            if (!final && !u.Source.TryTakeBatch(out pieces))
            {
                if (!u.Source.IsCompleted) return;
                if (u.Source.Error is { } completedError) { AbortRead(u, completedError); return; }
                final = true; pieces = [];
            }
            if (!u.Window.TrySend(final, out int sequence)) return;
            u.LastSend = u.LastActivity = Now; u.Sent += pieces.Count;
            var packet = new MapSyncPacket(pieces, u.Table.Block, u.Table.Pos.Copy(), final, null, final && u.IncludeWaypoints)
            { SessionId = u.Id, Sequence = sequence, Cancelled = u.Stop };
            using var trace = CartographyPerformanceTrace.Start(CoreClientAPI, "upload.send-main");
            CoreClientAPI.Network.GetChannel(CartographyTableConstants.CHANNEL_UPLOAD_TO_SERVER).SendPacket(packet);
            trace?.Detail($"session={u.Id} seq={sequence} pieces={pieces.Count} final={final}");
            if (trace != null) trace.AlwaysLog = sequence == 0 || final;
            if (sequence == 0 || final) LogUpload(u, final ? "final-sent" : "first-sent");
        }
        void AbortRead(Upload u, Exception error)
        {
            // Finish any already-committed prefix without claiming the full map
            // was read. The final packet also releases the server session.
            Notice("Cannot finish reading the local map: " + error.Message);
            u.Stop = u.Released = true; u.IncludeWaypoints = false; u.Source.Dispose();
            PumpUpload(u);
        }
        void LogUpload(Upload u, string reason)
        {
            if (!Settings.VerboseDebug) return;
            var p = u.Source.Progress;
            KsCartographyTableModSystem.DebugLog(CoreClientAPI, $"[perf] upload.stream session={u.Id} event={reason} sent={u.Sent} scanned={p.ScannedPieces} prepared={p.PreparedPieces} queuedBatches={p.QueuedBatches} readBytes={p.ReadBytes} openMs={p.OpenMs:F1} readMs={p.ReadMs:F1} deserializeMs={p.DeserializeMs:F1} backpressureMs={p.QueueWaitMs:F1} elapsedMs={p.ElapsedMs:F1}");
        }
        void OnAck(MapTransferAck ack)
        {
            if (disposed || ack == null) return;
            if (download is { } d && ack.SessionId == d.Id && !ack.Success) { FailDownload(ack.Error); return; }
            if (upload is not { } u || ack.SessionId != u.Id || !u.Window.Waiting || ack.Sequence != u.Window.NextSequence) return;
            if (!ack.Success) { FailUpload(ack.Error); return; }
            double ms = (Now - u.LastSend) * 1000;
            if (ms >= 100 && Now - u.LastLog > 5)
            { KsCartographyTableModSystem.DebugLog(CoreClientAPI, $"[perf] upload.ack session={u.Id} seq={ack.Sequence} roundTripMs={ms:F1}"); u.LastLog = Now; }
            u.Window.Acknowledge(ack.Sequence); u.LastActivity = Now;
            if (u.Window.Complete)
            {
                LogUpload(u, "committed"); u.Source.Dispose();
                if (Live(u.Table)) u.Table.SetWriting(false);
                if (u.Released) upload = null;
            }
            else PumpUpload(u);
        }
        void FailUpload(string error)
        {
            if (upload is not { } u) return;
            upload = null; u.Source.Dispose();
            if (Live(u.Table)) u.Table.SetWriting(false);
            Notice(error);
        }
        internal bool HasCartographyUploadSession(IPlayer player, Block block) => upload?.Table.Block == block;
        internal bool StartCartographyUploadSession(CartographyAction action, IWorldAccessor world, IPlayer player, BlockPos pos, Block block, BlockEntityCartographyTable table)
        {
            if (upload != null || download != null) { Notice("The previous cartography transfer is still finishing."); return false; }
            upload = new() { Table = table, Source = playerMapManager.StartUpload(table, Math.Clamp(Settings.ChunksPerPacket, 1, TransferProtocol.MaximumPieces)), IncludeWaypoints = Settings.WaypointUpload };
            table.SetWriting(true);
            KsCartographyTableModSystem.ShowChatMessage(CoreClientAPI, player, CartographyTableLangCodes.SESSION_STARTED);
            return true;
        }
        internal bool ContinueCartographyUploadSession(IPlayer player, float seconds, Block block) => upload != null;
        internal void EndCartographyUploadSession(IPlayer player, Block block, BlockEntityCartographyTable table)
        {
            if (upload is { } u && u.Table == table)
            {
                u.Stop = u.Released = true;
                if (u.Window.Complete) upload = null;
                u.Source.Dispose();
                if (upload != null) PumpUpload(u);
            }
            table.SetWriting(false); table.ClearRecentInteraction(player);
        }
        internal void StartDownload(BlockEntityCartographyTable table)
        {
            if (download != null || upload != null) { Notice("The previous cartography transfer is still finishing."); return; }
            download = new() { Table = table, Snapshot = new ClientMapIdSnapshot(table.IsAdvanced ? MapPath : null), IncludeWaypoints = Settings.WaypointDownload };
            table.SetWriting(true);
            KsCartographyTableModSystem.ShowChatMessage(CoreClientAPI, CoreClientAPI.World.Player, CartographyTableLangCodes.SESSION_STARTED);
        }
        internal void StopDownload(BlockEntityCartographyTable table)
        {
            if (download is { } d && d.Table == table)
            {
                if (!d.Requested) { d.Snapshot.Dispose(); download = null; }
                else CancelDownload(d);
            }
            table.SetWriting(false); table.ClearRecentInteraction(CoreClientAPI.World.Player);
        }
        void CancelDownload(Download d)
        {
            if (!d.Requested || d.Stop) return;
            d.Stop = true;
            CoreClientAPI.Network.GetChannel(TransferProtocol.Channel).SendPacket(new MapDownloadRequest { SessionId = d.Id, Cancelled = true });
        }
        void DownloadAck(Download d, int sequence, string error = null)
        {
            if (disposed) return;
            CoreClientAPI.Network.GetChannel(TransferProtocol.Channel).SendPacket(new MapTransferAck
            { SessionId = d.Id, Sequence = sequence, Success = error == null, Error = error });
        }
        public void OnMapDownloadRequest(MapSyncPacket packet)
        {
            if (disposed || download is not { } d || packet.SessionId != d.Id) return;
            if (d.Pending || packet.Sequence != d.Expected || packet.Pieces == null || packet.Pieces.Count > TransferProtocol.MaximumPieces) return;
            if (packet.Pieces.Values.Any(p => p?.Pixels == null || p.Pixels.Length != 1024))
            { DownloadAck(d, packet.Sequence, "Invalid map pixels"); FailDownload("Invalid map data received."); return; }
            d.Pending = true; d.LastActivity = Now;
            if (downloadWorker == null)
            {
                string path = MapPath; var logger = CoreClientAPI.Logger;
                downloadWorker = new(() => new ClientMapDownloadStore(path, logger), callback => CoreClientAPI.Event.EnqueueMainThreadTask(callback, "cartography-download"));
            }
            double queued = Now;
            if (!downloadWorker.TryEnqueue(store =>
            {
                using var timing = CartographyPerformanceTrace.Start(CoreClientAPI, "download.store-worker");
                timing?.Detail($"session={d.Id} seq={packet.Sequence} pieces={packet.Pieces.Count} queueMs={(Now - queued) * 1000:F1} final={packet.IsFinalBatch}");
                if (timing != null) timing.AlwaysLog = packet.Sequence == 0 || packet.IsFinalBatch;
                store.Store(packet.Pieces); return true;
            }, _ =>
            {
                if (disposed || download != d) return;
                d.Pending = false; d.Expected++; d.Received += packet.Pieces.Count; d.LastActivity = Now;
                renderer ??= new MapDownloadRenderer(ChunkMapLayer, CoreClientAPI.Logger);
                renderer.QueueVisiblePieces(packet.Pieces);
                DownloadAck(d, packet.Sequence);
                if (packet.IsFinalBatch)
                {
                    KsCartographyTableModSystem.DebugLog(CoreClientAPI, $"[perf] download.committed session={d.Id} chunks={d.Received} elapsedMs={(Now - d.Started) * 1000:F1} cancelled={packet.Cancelled}");
                    download = null;
                    if (Live(d.Table)) FinalizeDownload(packet, CoreClientAPI.World.Player, d.Received);
                }
            }, error =>
            {
                if (disposed || download != d) return;
                DownloadAck(d, packet.Sequence, error.Message); FailDownload("Cannot save downloaded map: " + error.Message);
            }, packet.Pieces.Count * 16384L))
            { DownloadAck(d, packet.Sequence, "Client database queue is full"); FailDownload("Client database queue is full."); }
        }
        void FailDownload(string error)
        {
            if (download is not { } d) return;
            download = null; d.Snapshot?.Dispose();
            if (Live(d.Table)) d.Table.SetWriting(false);
            Notice(error);
        }
        private void FinalizeDownload(MapSyncPacket packet, IClientPlayer currentPlayer, int chunkCount)
        {
            BlockEntityCartographyTable blockEntity = (BlockEntityCartographyTable)CoreClientAPI.World.BlockAccessor.GetBlockEntity(packet.BlockPos);

            if (blockEntity == null)
            {
                CoreClientAPI.Logger.Error($"{CartographyTableConstants.MAP_EVENT} Cannot finalize download for null blockentity!");
                return;
            }

            double km2 = chunkCount * 0.001024;
            bool mapUpdated = km2 > 0;
            bool waypointsUpdated = packet.WaypointSyncResult?.Synced == true;
            if (!mapUpdated && blockEntity.IsAdvanced && !packet.Cancelled)
            {
                KsCartographyTableModSystem.ShowChatMessage(CoreClientAPI, currentPlayer, CartographyTableLangCodes.PLAYER_MAP_UP_TO_DATE);
            }
            if (!waypointsUpdated && Settings.WaypointDownload)
            {
                KsCartographyTableModSystem.ShowChatMessage(CoreClientAPI, currentPlayer, CartographyTableLangCodes.PLAYER_WAYPOINTS_UP_TO_DATE);
            }
            blockEntity.SetWriting(false);
            if (!mapUpdated && !waypointsUpdated)
            {
                return;
            }
            if (mapUpdated && blockEntity.IsAdvanced)
            {
                KsCartographyTableModSystem.ShowChatMessage(CoreClientAPI, currentPlayer, CartographyTableLangCodes.PLAYER_MAP_UPDATED, $"{km2:F1}");
            }
            if (waypointsUpdated)
            {
                if (packet.WaypointSyncResult.Added > 0)
                {
                    KsCartographyTableModSystem.ShowChatMessage(CoreClientAPI, currentPlayer, CartographyTableLangCodes.PLAYER_WAYPOINTS_ADDED, packet.WaypointSyncResult.Added.ToString());
                }
                if (packet.WaypointSyncResult.Edited > 0)
                {
                    KsCartographyTableModSystem.ShowChatMessage(CoreClientAPI, currentPlayer, CartographyTableLangCodes.PLAYER_WAYPOINTS_EDITED, packet.WaypointSyncResult.Edited.ToString());
                }
                if (packet.WaypointSyncResult.Deleted > 0)
                {
                    KsCartographyTableModSystem.ShowChatMessage(CoreClientAPI, currentPlayer, CartographyTableLangCodes.PLAYER_WAYPOINTS_DELETED, packet.WaypointSyncResult.Deleted.ToString());
                }
            }
        }

        public void Ponder(IClientPlayer byPlayer, BlockEntityCartographyTable blockEntity)
        {
            PalantirTravelPacket palantirTravel = new PalantirTravelPacket(
                [.. blockEntity.Map.PalantirWaypoints.Select(waypoint =>
                {
                    return new CoordsPacket(waypoint.X, waypoint.Y, waypoint.Z);
                })],
                new CoordsPacket(byPlayer.Entity.Pos.X, byPlayer.Entity.Pos.Y, byPlayer.Entity.Pos.Z)
            );
            CoreClientAPI.Network.GetChannel(CartographyTableConstants.CHANNEL_SEND_TO_PALANTIR).SendPacket(palantirTravel);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; CoreClientAPI.Event.UnregisterGameTickListener(tickListener);
            upload?.Source.Dispose(); download?.Snapshot?.Dispose(); upload = null; download = null;
            playerMapManager.Dispose(); downloadWorker?.Complete();
        }
    }
}
