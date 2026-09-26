using Kaisentlaia.KsCartographyTableMod.GameContent;
using Vintagestory.API.Util;

namespace KsCartographyTable.test.Unit;

public class TransferWindowShould
{
    [Test]
    public void PreserveAnEmptyClientInventoryAcrossTheActualNetworkSerializer()
    {
        var request = new MapDownloadRequest { SessionId = "reset-map", KnownIds = [] };
        var decoded = SerializerUtil.Deserialize<MapDownloadRequest>(SerializerUtil.Serialize(request));
        Assert.That(decoded.KnownIds, Is.Not.Null.And.Empty,
            "Protobuf omits empty repeated fields; a cleared client map must still be accepted.");
        request.KnownIds = [0, 123456789, 9000000000000];
        decoded = SerializerUtil.Deserialize<MapDownloadRequest>(SerializerUtil.Serialize(request));
        Assert.That(decoded.KnownIds, Is.EqualTo(request.KnownIds));
    }

    [Test]
    public void BoundOutstandingPacketsAndRequireOrderedCommitAcknowledgements()
    {
        var window = new TransferWindow();
        for (int sequence = 0; sequence < TransferProtocol.UploadCommitGroupSize; sequence++)
        {
            Assert.That(window.TrySend(false, out int sent), Is.True);
            Assert.That(sent, Is.EqualTo(sequence));
        }
        Assert.That(window.TrySend(false, out _), Is.False, "A slow disk must not create an unbounded packet backlog.");
        Assert.That(window.Acknowledge(1), Is.False, "Future acknowledgements cannot skip a write.");
        Assert.That(window.Waiting, Is.True);
        Assert.That(window.Acknowledge(0), Is.True);
        Assert.That(window.TrySend(false, out int next), Is.True);
        Assert.That(next, Is.EqualTo(TransferProtocol.UploadCommitGroupSize));
        Assert.That(window.Acknowledge(0), Is.False, "A duplicate must not acknowledge the next write.");
        Assert.That(window.Waiting, Is.True);
    }

    [Test]
    public void WaitForTheLastWriteBeforeFinalizingACancelledOrCompletedTransfer()
    {
        var window = new TransferWindow();
        Assert.That(window.TrySend(false, out int first), Is.True);
        Assert.That(window.TrySend(true, out int sequence), Is.True);
        Assert.That(window.TrySend(false, out _), Is.False, "Nothing may be queued after the final packet.");
        Assert.That(window.Complete, Is.False, "Sending the final packet is not proof of persistence.");
        Assert.That(window.Acknowledge(first), Is.True);
        Assert.That(window.Complete, Is.False, "Earlier packets do not complete a window containing a final packet.");
        window.Acknowledge(sequence);
        Assert.That(window.Complete, Is.True);
        Assert.That(window.TrySend(true, out _), Is.False);
        Assert.That(window.Acknowledge(sequence), Is.False);
    }
}
