using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Kaisentlaia.KsCartographyTableMod.GameContent
{
    // Construct and call on the client main thread, which owns curVisibleChunks.
    // Only the queue is shared with the vanilla map generation thread.
    internal sealed class MapDownloadRenderer
    {
        private static readonly FieldInfo VisibleChunksField = typeof(ChunkMapLayer)
            .GetField("curVisibleChunks", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ReadyPiecesField = typeof(ChunkMapLayer)
            .GetField("readyMapPieces", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly HashSet<FastVec2i> visibleChunks;
        private readonly ConcurrentQueue<ReadyMapPiece> readyPieces;

        internal MapDownloadRenderer(ChunkMapLayer layer, ILogger logger)
        {
            if (layer != null)
            {
                visibleChunks = VisibleChunksField?.GetValue(layer) as HashSet<FastVec2i>;
                readyPieces = ReadyPiecesField?.GetValue(layer) as ConcurrentQueue<ReadyMapPiece>;
            }

            if (visibleChunks == null || readyPieces == null)
            {
                logger?.Warning("[kscartographytable] Cannot refresh visible downloaded terrain with this map layer. Terrain is saved; reopen the map or use .map redraw to refresh it.");
            }
        }

        internal int QueueVisiblePieces(IReadOnlyDictionary<FastVec2i, MapPieceDB> pieces)
        {
            if (visibleChunks == null || readyPieces == null) return 0;

            int queued = 0;
            foreach (var piece in pieces)
            {
                if (piece.Value?.Pixels == null || !visibleChunks.Contains(piece.Key)) continue;

                // The normal ChunkMapLayer.OnTick path creates/updates textures.
                // Distant downloaded chunks stay on disk until the player views
                // them, avoiding a texture allocation for the entire shared map.
                readyPieces.Enqueue(new ReadyMapPiece { Cord = piece.Key, Pixels = piece.Value.Pixels });
                queued++;
            }
            return queued;
        }
    }
}
