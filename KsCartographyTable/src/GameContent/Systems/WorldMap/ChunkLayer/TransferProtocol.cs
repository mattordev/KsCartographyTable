using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace Kaisentlaia.KsCartographyTableMod.GameContent
{
    internal static class TransferProtocol
    {
        internal const string Channel = "kscartographytable-transfer-v3";
        internal const int MaximumPieces = 512;
        // ChunksPerPacket remains the requested count. This separate byte budget
        // prevents variable-sized map pieces from creating an unsafe wire packet.
        internal const int MaximumMapDataBytes = 512 * 1024;
        // Keep individual wire packets small, but amortize SQLite's durable commit
        // across a few packets. Four packets cap the in-flight map data at 2 MiB.
        internal const int UploadCommitGroupSize = 4;
        internal const int MaximumIds = 2_000_000;
        internal const double TimeoutSeconds = 120;
    }

    [ProtoContract]
    public sealed class MapTransferAck
    {
        [ProtoMember(1)] public string SessionId { get; set; }
        [ProtoMember(2)] public int Sequence { get; set; }
        [ProtoMember(3)] public bool Success { get; set; }
        [ProtoMember(4)] public string Error { get; set; }
    }

    [ProtoContract]
    public sealed class MapDownloadRequest
    {
        [ProtoMember(1)] public string SessionId { get; set; }
        [ProtoMember(2)] public string BlockId { get; set; }
        [ProtoMember(3)] public BlockPos Position { get; set; }
        [ProtoMember(4, IsPacked = true)] public ulong[] KnownIds { get; set; } = [];
        [ProtoMember(5)] public bool Cancelled { get; set; }
        [ProtoMember(6)] public bool IncludeWaypoints { get; set; }
    }

    // Uploads use a small bounded window so the server can commit several safe-sized
    // packets together. Downloads still use this as a one-packet window because their
    // sender checks Waiting before reading the next batch.
    internal sealed class TransferWindow
    {
        private readonly Queue<bool> pending = new();
        private int nextSendSequence;
        private bool finalQueued;
        internal int NextSequence { get; private set; }
        internal bool Waiting => pending.Count > 0;
        internal bool CanSend => !Complete && !finalQueued && pending.Count < TransferProtocol.UploadCommitGroupSize;
        internal bool Complete { get; private set; }
        internal bool TrySend(bool isFinal, out int sequence)
        {
            sequence = nextSendSequence;
            if (!CanSend) return false;
            pending.Enqueue(isFinal);
            finalQueued = isFinal;
            nextSendSequence++;
            return true;
        }
        internal bool Acknowledge(int sequence)
        {
            if (!Waiting || sequence != NextSequence) return false;
            Complete = pending.Dequeue();
            NextSequence++;
            return true;
        }
    }
}
