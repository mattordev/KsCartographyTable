
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kaisentlaia.KsCartographyTableMod.API.Common;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Kaisentlaia.KsCartographyTableMod.GameContent
{
    [ProtoContract]
    public class WaypointSyncResult
    {
        [ProtoMember(1)]
        public int Added;
        [ProtoMember(2)]
        public int Edited;
        [ProtoMember(3)]
        public int Deleted;
        [ProtoMember(4)]
        public int Rejected;
        [ProtoMember(5)]
        public bool Synced;

        public WaypointSyncResult()
        {
        }

        public WaypointSyncResult(int added, int edited, int rejected, int deleted)
        {
            Added = added;
            Edited = edited;
            Rejected = rejected;
            Deleted = deleted;
            Synced = Added > 0 || Edited > 0 || Deleted > 0;
        }
    }
    public class ServerWaypointManager
    {
        public ICoreServerAPI CoreServerAPI;
        WorldMapManager WorldMapManager;
        WaypointMapLayer waypointMapLayer;
        public WaypointMapLayer WaypointMapLayer
        {
            get
            {
                if (waypointMapLayer == null)
                {
                    WorldMapManager = CoreServerAPI.ModLoader.GetModSystem<WorldMapManager>();
                    if (WorldMapManager != null)
                    {
                        waypointMapLayer = WorldMapManager.MapLayers.FirstOrDefault((MapLayer ml) => ml is WaypointMapLayer) as WaypointMapLayer;
                    }
                }

                return waypointMapLayer;
            }
        }
        internal string modDataPath;

        public ServerWaypointManager(ICoreServerAPI api)
        {
            CoreServerAPI = api;
            modDataPath = Path.Combine(
                GamePaths.DataPath,
                "ModData",
                CoreServerAPI.World.SavegameIdentifier,
                CartographyTableConstants.MOD_ID
            );
            GamePaths.EnsurePathExists(modDataPath);
        }

        private List<Waypoint> GetPlayerWaypoints(IPlayer player)
        {
            List<Waypoint> waypoints = [];
            if (player == null)
            {
                CoreServerAPI?.Logger.Error($"{CartographyTableConstants.MAP_EVENT} GetPlayerWaypoints for null player!");
                return waypoints;
            }
            if (WaypointMapLayer != null)
            {
                waypoints = WaypointMapLayer.Waypoints.FindAll(PlayerWaypoint => PlayerWaypoint.OwningPlayerUid == player.PlayerUID);
            }
            return waypoints;
        }

        private bool EnsureWaypointGuids(IServerPlayer player)
        {
            if (WaypointMapLayer == null) return false;
            bool changed = false;
            foreach (Waypoint w in WaypointMapLayer.Waypoints)
            {
                if (w.OwningPlayerUid != player.PlayerUID) continue;
                if (string.IsNullOrEmpty(w.Guid))
                {
                    w.Guid = Guid.NewGuid().ToString();
                    KsCartographyTableModSystem.DebugLog(CoreServerAPI, $"Assigned missing Guid to waypoint '{w.Title}' for {player.PlayerName}");
                    changed = true;
                }
            }
            return changed;
        }

        public void ResendWaypointsToAllPlayers()
        {
            CoreServerAPI.World.AllOnlinePlayers.Foreach(player =>
            {
                WorldMapManager.SendMapDataToClient(WaypointMapLayer, player as IServerPlayer, SerializerUtil.Serialize(GetPlayerWaypoints(player)));
            });
        }

        public void ResendWaypointsToPlayer(IServerPlayer toPlayer)
        {
            using var timing = CartographyPerformanceTrace.Start(CoreServerAPI, "waypoints.send");
            List<Waypoint> list = [];
            if (toPlayer == null)
            {
                CoreServerAPI.Logger.Error($"{CartographyTableConstants.MAP_EVENT} Resending waypoints to null player!");
                return;
            }
            foreach (Waypoint waypoint in WaypointMapLayer.Waypoints)
            {
                if (toPlayer.PlayerUID == waypoint.OwningPlayerUid)
                {
                    list.Add(waypoint);
                }
            }
            timing?.Mark("select");
            var payload = SerializerUtil.Serialize(list);
            timing?.Mark("serialize");
            WorldMapManager.SendMapDataToClient(WaypointMapLayer, toPlayer, payload);
            timing?.Mark("send");
            timing?.Detail($"waypoints={list.Count} payloadBytes={payload.Length}");
            if (timing != null) timing.AlwaysLog = true;
        }

        public List<Waypoint> GetWaypointsWithGroupId()
        {
            return WaypointMapLayer.Waypoints.FindAll(PlayerWaypoint => PlayerWaypoint.OwningPlayerGroupId != -1);
        }
        public TextCommandResult ClearAllWaypoints(bool dryRun, IServerPlayer forPlayer, bool mapOnly)
        {
            string additionalAction = !mapOnly ? $" and mark{(dryRun ? "" : "ed")} them to be deleted on the cartography table at the next transcription" : "";
            // TODO localization
            if (forPlayer != null)
            {
                List<Waypoint> waypointsToWipe = GetPlayerWaypoints(forPlayer);

                int waypointsCount = waypointsToWipe.Count;
                if (!dryRun)
                {
                    waypointsToWipe.ForEach(waypoint =>
                    {
                        if (!mapOnly)
                        {
                            AddDeletedWaypointId(waypoint, forPlayer);
                        }
                        WaypointMapLayer.Waypoints.Remove(waypoint);
                    });
                    ResendWaypointsToPlayer(forPlayer);
                    CoreServerAPI.SendMessage(forPlayer, GlobalConstants.GeneralChatGroup, $"Deleted {waypointsCount} waypoints from your map{additionalAction}.", EnumChatType.Notification);
                    return TextCommandResult.Success();
                }
                CoreServerAPI.SendMessage(forPlayer, GlobalConstants.GeneralChatGroup, $"Will delete {waypointsCount} waypoints from your map{additionalAction}. Run '.kct waypoints wipe {(mapOnly ? "maponly" : "mapandtable")} confirm' to confirm.", EnumChatType.Notification);
                return TextCommandResult.Success();

            }
            else
            {
                int waypointsCount = WaypointMapLayer.Waypoints.Count;
                if (!dryRun)
                {
                    if (!mapOnly)
                    {
                        WaypointMapLayer.Waypoints.ForEach(waypoint =>
                        {
                            AddDeletedWaypointId(waypoint, CoreServerAPI.World.PlayerByUid(waypoint.OwningPlayerUid));
                        });
                    }
                    WaypointMapLayer.Waypoints.Clear();
                    ResendWaypointsToAllPlayers();
                    return TextCommandResult.Success($"Deleted {waypointsCount} waypoints from all players' maps{additionalAction}.");
                }
                return TextCommandResult.Success($"Will delete {waypointsCount} waypoints from all players' maps{additionalAction}. Run '/kct waypoints wipe {(mapOnly ? "maponly" : "mapandtable")} confirm' to confirm.");
            }
        }
        internal void AddDeletedWaypointId(Waypoint deletedWaypoint, IPlayer byPlayer)
        {
            List<string> deletedWaypoints = GetDeletedWaypointsIds(byPlayer);
            deletedWaypoints.Add(deletedWaypoint.Guid);
            SaveDeletedWaypointsIds(deletedWaypoints, byPlayer);
        }

        internal string GetWaypointsFilePath(IPlayer byPlayer)
        {
            return Path
                .Combine(modDataPath, Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(byPlayer.PlayerUID)
                )
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=') + ".json");
        }

        public List<string> GetDeletedWaypointsIds(IPlayer byPlayer)
        {
            RenameWaypointsFile(byPlayer);
            string deletedWaypointsFilePath = GetWaypointsFilePath(byPlayer);
            if (!File.Exists(deletedWaypointsFilePath)) return [];

            try
            {
                string json = File.ReadAllText(deletedWaypointsFilePath);
                var ids = JsonUtil.FromString<List<string>>(json);
                if (ids != null)
                {
                    return ids;
                }
                return [];
            }
            catch (Exception ex)
            {
                CoreServerAPI.Logger.Error("Failed to load player deleted waypoints: {0}", ex);
                return [];
            }
        }

        private void RenameWaypointsFile(IPlayer byPlayer)
        {
            string filenameV1 = Path.Combine(modDataPath, byPlayer.PlayerUID + ".json");
            string filenameV2 = Path.Combine(modDataPath, Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(byPlayer.PlayerUID)
            ).TrimEnd('=') + ".json");
            string filenameV3 = GetWaypointsFilePath(byPlayer);
            if (Path.Exists(filenameV1))
            {
                if (!Path.Exists(filenameV3))
                {
                    File.Move(filenameV1, filenameV3);
                }
                File.Delete(filenameV1);
            }
            if (filenameV2 != filenameV3 && Path.Exists(filenameV2))
            {
                if (!Path.Exists(filenameV3))
                {
                    File.Move(filenameV2, filenameV3);
                }
                File.Delete(filenameV2);
            }
        }

        private void SaveDeletedWaypointsIds(List<string> deletedWaypointIds, IPlayer byPlayer)
        {
            RenameWaypointsFile(byPlayer);
            string deletedWaypointsFilePath = GetWaypointsFilePath(byPlayer);
            try
            {
                string json = JsonUtil.ToString(deletedWaypointIds.ToList());
                File.WriteAllText(deletedWaypointsFilePath, json);
            }
            catch (Exception ex)
            {
                CoreServerAPI.Logger.Error("Failed to save player deleted waypoints: {0}", ex);
            }
        }

        internal sealed record WaypointSnapshot(string Uid, DateTime LastDownload, List<Waypoint> Current, List<string> DeletedIds, int WorldCount);
        internal sealed record WaypointDownloadPlan(WaypointSnapshot Snapshot, List<Waypoint> Final, WaypointSyncResult Result);

        internal WaypointSnapshot Capture(IPlayer player, BlockEntityCartographyTable table, bool readDeleted = false)
        {
            using var timing = CartographyPerformanceTrace.Start(CoreServerAPI, "waypoints.snapshot-main");
            if (player is IServerPlayer sp && EnsureWaypointGuids(sp)) ResendWaypointsToPlayer(sp);
            timing?.Mark("ensureGuidsAndResend");
            return new(player.PlayerUID, table.GetPlayerLastDownload(player),
                GetPlayerWaypoints(player).Select(CloneWaypoint).ToList(),
                readDeleted ? GetDeletedWaypointsIds(player) : [], WaypointMapLayer?.Waypoints.Count ?? 0);
        }

        private static Waypoint CloneWaypoint(Waypoint w) => new CartographyWaypoint(w)
        { Position = w.Position.Clone(), OwningPlayerGroupId = w.OwningPlayerGroupId };

        internal WaypointSyncResult UpdateTableWaypoints(IServerPlayer player, BlockPos pos, ServerMapDB db)
        {
            var table = CoreServerAPI.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityCartographyTable;
            return table == null ? new(0, 0, 0, 0) : StoreTableWaypoints(Capture(player, table, true), db);
        }

        // Only detached waypoint copies and worker-owned SQLite objects are used here.
        internal WaypointSyncResult StoreTableWaypoints(WaypointSnapshot snapshot, ServerMapDB mapDB)
        {
            using var timing = CartographyPerformanceTrace.Start(CoreServerAPI, "waypoints.upload-worker");
            DateTime playerLastDownload = snapshot.LastDownload;
            List<CartographyWaypoint> playerSharedDbWaypoints = mapDB.GetPlayerSharedWaypoints(snapshot.Uid);
            List<Waypoint> playerCurrentWaypoints = snapshot.Current;
            timing?.Detail($"worldWaypoints={snapshot.WorldCount} playerWaypoints={playerCurrentWaypoints.Count} sharedWithPlayer={playerSharedDbWaypoints.Count}");
            timing?.Mark("readPlayerWaypoints");

            // Build the lookups once. Repeated List.Find calls make even an
            // unchanged upload compare every waypoint with every other one.
            var sharedGuids = playerSharedDbWaypoints.Select(w => w.Guid).ToHashSet(StringComparer.Ordinal);
            var currentByGuid = playerCurrentWaypoints.ToLookup(w => w.Guid, StringComparer.Ordinal);
            List<CartographyWaypoint> newWaypoints = [.. playerCurrentWaypoints.Where(w => !sharedGuids.Contains(w.Guid)).Select(waypoint => new CartographyWaypoint(waypoint))];

            List<CartographyWaypoint> waypointsToCreate = [];
            List<CartographyWaypoint> existingWaypointsToTrack = [];
            List<CartographyWaypoint> waypointsToUpdate = [.. playerSharedDbWaypoints
                    .Select(sharedWaypoint =>
                    {
                        var currentWaypoint = currentByGuid[sharedWaypoint.Guid].FirstOrDefault(current =>
                            (current.Color != sharedWaypoint.Color ||
                            current.Title != sharedWaypoint.Title ||
                            current.Icon != sharedWaypoint.Icon ||
                            current.Pinned != sharedWaypoint.Pinned));

                        if (currentWaypoint != null)
                        {
                            sharedWaypoint.Color = currentWaypoint.Color;
                            sharedWaypoint.Title = currentWaypoint.Title;
                            sharedWaypoint.Icon = currentWaypoint.Icon;
                            sharedWaypoint.Pinned = currentWaypoint.Pinned;

                            return sharedWaypoint;
                        }

                        return null;
                    })
                    .Where(w => w != null)];
            timing?.Mark("classify");
            newWaypoints.ForEach(waypoint =>
            {
                CartographyWaypoint matching = mapDB.GetMatchingWaypoint(waypoint);
                if (matching != null)
                {
                    waypoint.ParentGuid = matching.Guid;
                    waypoint.LastUpdated = matching.LastUpdated;
                    existingWaypointsToTrack.Add(waypoint);
                }
                else
                {
                    waypointsToCreate.Add(waypoint);
                }
            });
            timing?.Detail($"matchQueries={newWaypoints.Count} create={waypointsToCreate.Count} track={existingWaypointsToTrack.Count}");
            timing?.Mark("matchNew");
            mapDB.CreateWaypoints(waypointsToCreate);
            mapDB.CreateWaypoints(existingWaypointsToTrack);
            timing?.Mark("create");

            List<CartographyWaypoint> rejectedWaypoints = [.. waypointsToUpdate.Where(w => w.LastUpdated > playerLastDownload)];

            List<CartographyWaypoint> updatedWaypoints = [.. waypointsToUpdate.Where(w => w.LastUpdated <= playerLastDownload)];

            mapDB.UpdateWaypoints(updatedWaypoints);
            timing?.Mark("update");

            var deletedIds = snapshot.DeletedIds;
            timing?.Mark("readDeletionHistory");
            List<CartographyWaypoint> deletedWaypoints = mapDB.GetWaypointsToDelete(deletedIds);
            timing?.Mark("findDeleted");
            mapDB.DeleteWaypoints(deletedWaypoints);
            timing?.Mark("delete");
            timing?.Detail($"deletionHistory={deletedIds.Count} deletedRows={deletedWaypoints.Count} edited={updatedWaypoints.Count} rejected={rejectedWaypoints.Count}");
            if (timing != null) timing.AlwaysLog = true;

            return new WaypointSyncResult(waypointsToCreate.Count, updatedWaypoints.Count, rejectedWaypoints.Count, deletedWaypoints.Count);
        }

        internal WaypointDownloadPlan ReadPlayerWaypoints(WaypointSnapshot snapshot, ServerMapDB db)
        {
            var current = snapshot.Current.Select(CloneWaypoint).ToList();
            var shared = db.GetPlayerSharedWaypoints(snapshot.Uid);
            var additions = new List<CartographyWaypoint>();
            int created = 0, edited = 0, deleted = 0;
            var tracked = shared.Select(w => w.Guid).ToHashSet();
            foreach (var parent in db.GetNewWaypointsForPlayer(snapshot.Uid))
            {
                var existing = current.Find(w => w.Position == parent.Position && w.Title == parent.Title && w.Icon == parent.Icon);
                if (existing != null && tracked.Contains(existing.Guid)) continue;
                if (existing == null)
                {
                    existing = CloneWaypoint(parent);
                    existing.Guid = Guid.NewGuid().ToString();
                    existing.OwningPlayerUid = snapshot.Uid;
                    current.Add(existing);
                    created++;
                }
                additions.Add(new(existing) { ParentGuid = parent.Guid, LastUpdated = parent.LastUpdated });
                tracked.Add(existing.Guid);
            }
            foreach (var w in shared)
            {
                if (current.Any(c => c.Guid == w.Guid)) continue;
                current.Add(CloneWaypoint(w));
                created++;
            }
            foreach (var update in db.GetUpdatedWaypointsForPlayer(snapshot.Uid, snapshot.LastDownload))
            {
                var w = current.Find(c => c.Guid == update.Guid);
                if (w == null || SameWaypoint(w, update)) continue;
                w.Color = update.Color; w.Title = update.Title; w.Icon = update.Icon; w.Pinned = update.Pinned;
                edited++;
            }
            foreach (var removed in db.GetDeletedWaypointsForPlayer(snapshot.Uid, snapshot.LastDownload))
                deleted += current.RemoveAll(w => w.Guid == removed.Guid);
            db.CreateWaypoints(additions);
            return new(snapshot, current, new(created, edited, 0, deleted));
        }

        private static bool SameWaypoint(Waypoint a, Waypoint b) =>
            a.Color == b.Color && a.Title == b.Title && a.Icon == b.Icon && a.Pinned == b.Pinned && a.Position == b.Position;

        // Merge on the game thread. Preserve edits/deletions made while SQL was running.
        internal WaypointSyncResult ApplyPlayerWaypoints(IPlayer player, WaypointDownloadPlan plan)
        {
            var before = plan.Snapshot.Current.ToDictionary(w => w.Guid);
            var after = plan.Final.ToDictionary(w => w.Guid);
            var live = WaypointMapLayer.Waypoints;
            int added = 0, edited = 0, deleted = 0;
            foreach (var w in live.Where(w => w.OwningPlayerUid == player.PlayerUID).ToList())
            {
                if (!before.TryGetValue(w.Guid, out var original) || !SameWaypoint(w, original)) continue;
                if (!after.TryGetValue(w.Guid, out var replacement))
                {
                    live.Remove(w); AddDeletedWaypointId(w, player); deleted++;
                }
                else if (!SameWaypoint(w, replacement))
                {
                    w.Color = replacement.Color; w.Title = replacement.Title;
                    w.Icon = replacement.Icon; w.Pinned = replacement.Pinned; edited++;
                }
            }
            var liveIds = live.Select(w => w.Guid).ToHashSet();
            foreach (var w in plan.Final)
                if (!before.ContainsKey(w.Guid) && liveIds.Add(w.Guid)) { live.Add(w); added++; }
            if (added + edited + deleted > 0) ResendWaypointsToPlayer(player as IServerPlayer);
            return new(added, edited, 0, deleted);
        }

        internal WaypointSyncResult UpdatePlayerWaypoints(IPlayer player, BlockEntityCartographyTable table, ServerMapDB db)
            => ApplyPlayerWaypoints(player, ReadPlayerWaypoints(Capture(player, table), db));
    }
}
