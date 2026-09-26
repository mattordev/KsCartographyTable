using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kaisentlaia.KsCartographyTableMod.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Kaisentlaia.KsCartographyTableMod.GameContent
{
	public class PlayerMapManager : IDisposable
	{
        WorldMapManager WorldMapManager;
		public ICoreClientAPI CoreClientAPI;
        private readonly List<IMapUploadSource> uploads = [];
        ChunkMapLayer chunkMapLayer;
        public ChunkMapLayer ChunkMapLayer
        {
            get {
                if (chunkMapLayer == null)
                {
                    WorldMapManager = CoreClientAPI.ModLoader.GetModSystem<WorldMapManager>();
                    if (WorldMapManager != null)
                    {
                        chunkMapLayer = WorldMapManager.MapLayers.FirstOrDefault((MapLayer ml) => ml is ChunkMapLayer) as ChunkMapLayer;                    
                    }
                }

                return chunkMapLayer;
            }
        }
		public PlayerMapManager(ICoreClientAPI api) {
			CoreClientAPI = api;
            WorldMapManager = CoreClientAPI.ModLoader.GetModSystem<WorldMapManager>();
		}

        internal IMapUploadSource StartUpload(BlockEntityCartographyTable blockEntity, int batchSize)
        {
            for (int i = uploads.Count - 1; i >= 0; i--)
            {
                if (uploads[i].IsCompleted || uploads[i].IsCanceled)
                {
                    uploads[i].Dispose();
                    uploads.RemoveAt(i);
                }
            }

            // Capture everything belonging to the game here. The source copies
            // the IDs before starting its worker and never touches this entity.
            string mapPath = blockEntity.IsAdvanced
                ? Path.Combine(GamePaths.DataPath, "Maps", CoreClientAPI.World.SavegameIdentifier + ".db")
                : null;
            var source = new SqliteMapUploadSource(mapPath,
                blockEntity.Map?.ExploredAreasIds ?? [], batchSize);
            uploads.Add(source);
            return source;
        }

        public void Dispose()
        {
            foreach (var upload in uploads) upload.Dispose();
            uploads.Clear();
            // The vanilla map layer owns its connection; never close it here.
        }
    }
}
