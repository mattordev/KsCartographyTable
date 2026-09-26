using System;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace Kaisentlaia.KsCartographyTableMod.GameContent
{
    internal static class TransferProtocol
    {
        internal const string Channel = "kscartographytable-transfer-v3";
        internal const int MaximumPieces = 256;
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

    // One outstanding packet keeps disk latency from becoming a network/work queue.
    // Only an exact acknowledgement advances the session, including its final packet.
    internal sealed class TransferWindow
    {
        internal int NextSequence { get; private set; }
        internal bool Waiting { get; private set; }
        internal bool Complete { get; private set; }
        private bool final;
        internal bool TrySend(bool isFinal, out int sequence)
        {
            sequence = NextSequence;
            if (Waiting || Complete) return false;
            Waiting = true;
            final = isFinal;
            return true;
        }
        internal bool Acknowledge(int sequence)
        {
            if (!Waiting || sequence != NextSequence) return false;
            Waiting = false;
            Complete = final;
            NextSequence++;
            return true;
        }
    }
}
