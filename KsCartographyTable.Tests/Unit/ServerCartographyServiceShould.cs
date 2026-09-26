using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Kaisentlaia.KsCartographyTableMod.API.Common;
using Kaisentlaia.KsCartographyTableMod.API.Server;
using Kaisentlaia.KsCartographyTableMod.GameContent;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace KsCartographyTable.test.Unit;

[NonParallelizable]
public class ServerCartographyServiceShould
{
    [Test]
    public async Task ReturnFromTheUploadHandlerWhileSqlIsLockedAndAcknowledgeOnlyAfterCommit()
    {
        SQLitePCL.Batteries_V2.Init();
        string root = Directory.CreateTempSubdirectory("KctServerTransfer-").FullName;
        var api = Substitute.For<ICoreServerAPI>();
        var world = Substitute.For<IServerWorldAccessor>();
        api.World.Returns(world);
        ((ICoreAPI)api).World.Returns(world);
        api.Side.Returns(EnumAppSide.Server);
        var logger = Substitute.For<ILogger>();
        api.Logger.Returns(logger); world.Logger.Returns(logger);
        world.SavegameIdentifier.Returns(root);
        api.GetOrCreateDataPath(Arg.Any<string>()).Returns(root);
        Settings.Init(api, "test"); Settings.Load();
        // This headless test has no language assets; immersive mode suppresses
        // chat while retaining the real transfer, database and metadata paths.
        Settings.ImmersiveMode = true;
        var callbacks = new ConcurrentQueue<Action>();
        api.Event.When(e => e.EnqueueMainThreadTask(Arg.Any<Action>(), Arg.Any<string>()))
            .Do(call => callbacks.Enqueue(call.Arg<Action>()));
        var channel = Substitute.For<IServerNetworkChannel>();
        channel.RegisterMessageType<MapSyncPacket>().Returns(channel);
        channel.RegisterMessageType<MapTransferAck>().Returns(channel);
        channel.RegisterMessageType<MapDownloadRequest>().Returns(channel);
        channel.RegisterMessageType<KctCommandPacket>().Returns(channel);
        api.Network.RegisterChannel(Arg.Any<string>()).Returns(channel);
        api.Network.GetChannel(Arg.Any<string>()).Returns(channel);
        var acknowledgements = new List<MapTransferAck>();
        var downloads = new List<MapSyncPacket>();
        channel.When(c => c.SendPacket(Arg.Any<MapTransferAck>(), Arg.Any<IServerPlayer[]>()))
            .Do(call => acknowledgements.Add(call.Arg<MapTransferAck>()));
        channel.When(c => c.SendPacket(Arg.Any<MapSyncPacket>(), Arg.Any<IServerPlayer[]>()))
            .Do(call => downloads.Add(call.Arg<MapSyncPacket>()));
        var player = Substitute.For<IServerPlayer>();
        player.PlayerUID.Returns("alice");
        player.Entity.Returns(new EntityPlayer());
        var table = new BlockEntityCartographyTable
        {
            Api = api, Side = EnumAppSide.Server, Pos = new BlockPos(0, 0, 0),
            Block = new BlockAdvancedCartographyTable { BlockId = 123 }, Map = new CartographyMap(api)
        };
        world.BlockAccessor.GetBlockEntity(table.Pos).Returns(table);
        string legacyDirectory = Path.Combine(root, CartographyTableConstants.MOD_ID);
        Directory.CreateDirectory(legacyDirectory);
        string legacyError = null;
        using (var legacy = new ServerMapDB(api, logger))
        {
            Assert.That(legacy.OpenOrCreate(Path.Combine(legacyDirectory, "123.db"), ref legacyError, true, true, false),
                Is.True, legacyError);
            legacy.StoreMapPieces(new()
            {
                [new(99, 20)] = new() { Pixels = Enumerable.Repeat(unchecked((int)0xff778899), 1024).ToArray() }
            }, "legacy-player");
        }
        var service = new ServerCartographyService(api);
        string id = Guid.NewGuid().ToString();
        string path = Path.Combine(root, CartographyTableConstants.MOD_ID, table.StorageId + ".db");
        var handler = typeof(ServerCartographyService).GetMethod("OnMapUploadRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Receive(int sequence, int x, bool final = false) => handler.Invoke(service, [player,
            new MapSyncPacket(final ? [] : new() { [new(x, 20)] = new() { Pixels = Enumerable.Repeat(x, 1024).ToArray() } },
                table.Block, table.Pos, final, null, false) { SessionId = id, Sequence = sequence }]);
        async Task PumpUntil(Func<bool> predicate)
        {
            var timer = Stopwatch.StartNew();
            while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                while (callbacks.TryDequeue(out var callback)) callback();
                await Task.Delay(10);
            }
            Assert.That(predicate(), Is.True, "No commit acknowledgement arrived.");
        }
        try
        {
            for (int sequence = 0; sequence < TransferProtocol.UploadCommitGroupSize; sequence++)
                Receive(sequence, 10 + sequence);
            Assert.That(acknowledgements, Is.Empty, "Receiving a packet is not a commit.");
            await PumpUntil(() => acknowledgements.Count == TransferProtocol.UploadCommitGroupSize);
            Assert.That(acknowledgements[0].Success, Is.True, acknowledgements[0].Error);
            using (var competingWriter = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                competingWriter.Open();
                using var transaction = competingWriter.BeginTransaction();
                var timer = Stopwatch.StartNew();
                for (int offset = 0; offset < TransferProtocol.UploadCommitGroupSize; offset++)
                    Receive(TransferProtocol.UploadCommitGroupSize + offset, 20 + offset);
                Assert.That(timer.ElapsedMilliseconds, Is.LessThan(1000), "The interaction handler must not wait for a SQLite writer.");
                await Task.Delay(100);
                while (callbacks.TryDequeue(out var callback)) callback();
                Assert.That(acknowledgements, Has.Count.EqualTo(TransferProtocol.UploadCommitGroupSize),
                    "The second commit group cannot be acknowledged while the write lock is held.");
                transaction.Commit();
            }
            int committedPackets = TransferProtocol.UploadCommitGroupSize * 2;
            await PumpUntil(() => acknowledgements.Count == committedPackets);
            Assert.That(acknowledgements[^1].Success, Is.True, acknowledgements[^1].Error);
            Receive(committedPackets, 0, final: true);
            await PumpUntil(() => acknowledgements.Count == committedPackets + 1);
            Assert.That(acknowledgements[^1].Success, Is.True, acknowledgements[^1].Error);
            int totalStoredPieces = committedPackets + 1;
            Assert.That(table.Map.ExploredAreasIds, Has.Count.EqualTo(totalStoredPieces),
                "Final table metadata must include migrated and newly committed packets.");
            using var reader = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            reader.Open();
            using var command = reader.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM mappiece";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(totalStoredPieces));

            // The same uploader returns with no local IDs. Exercise the real
            // request handler, not just the SQL query used by the new handshake.
            string downloadId = Guid.NewGuid().ToString();
            var requestHandler = typeof(ServerCartographyService).GetMethod("OnDownloadRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var ackHandler = typeof(ServerCartographyService).GetMethod("OnDownloadAck", BindingFlags.Instance | BindingFlags.NonPublic)!;
            requestHandler.Invoke(service, [player, new MapDownloadRequest
            { SessionId=downloadId, BlockId="123", Position=table.Pos, KnownIds=[], IncludeWaypoints=false }]);
            await PumpUntil(() => downloads.Count == 1);
            Assert.That(downloads[0].Pieces, Has.Count.EqualTo(totalStoredPieces));
            Assert.That(downloads[0].IsFinalBatch, Is.True);
            Assert.That(service.HasCartographyDownloadSession(player, table.Block), Is.True,
                "Sending the final batch must not finish before client persistence.");
            ackHandler.Invoke(service, [player, new MapTransferAck { SessionId=downloadId, Sequence=1, Success=true }]);
            Assert.That(service.HasCartographyDownloadSession(player, table.Block), Is.True);
            ackHandler.Invoke(service, [player, new MapTransferAck { SessionId=downloadId, Sequence=0, Success=true }]);
            Assert.That(service.HasCartographyDownloadSession(player, table.Block), Is.False);

            string cancelledId = Guid.NewGuid().ToString();
            requestHandler.Invoke(service, [player, new MapDownloadRequest
            { SessionId=cancelledId, BlockId="123", Position=table.Pos, KnownIds=[], IncludeWaypoints=false }]);
            requestHandler.Invoke(service, [player, new MapDownloadRequest { SessionId=cancelledId, Cancelled=true }]);
            await PumpUntil(() => downloads.Count == 2);
            Assert.That(downloads[1].Cancelled, Is.True);
            Assert.That(downloads[1].Pieces, Is.Empty, "Cancellation while preparing must not start reading/sending terrain.");
            ackHandler.Invoke(service, [player, new MapTransferAck { SessionId=cancelledId, Sequence=0, Success=true }]);
            Assert.That(service.HasCartographyDownloadSession(player, table.Block), Is.False);

            string aliceUpload = Guid.NewGuid().ToString();
            handler.Invoke(service, [player, new MapSyncPacket(new()
            {
                [new(50, 20)] = new() { Pixels = Enumerable.Repeat(unchecked((int)0xff112233), 1024).ToArray() }
            }, table.Block, table.Pos) { SessionId = aliceUpload, Sequence = 0 }]);
            var bob = Substitute.For<IServerPlayer>();
            bob.PlayerUID.Returns("bob");
            bob.Entity.Returns(new EntityPlayer());
            int acknowledgementsBeforeBob = acknowledgements.Count;
            handler.Invoke(service, [bob, new MapSyncPacket(new()
            {
                [new(51, 20)] = new() { Pixels = Enumerable.Repeat(unchecked((int)0xff445566), 1024).ToArray() }
            }, table.Block, table.Pos) { SessionId = Guid.NewGuid().ToString(), Sequence = 0 }]);
            Assert.That(acknowledgements, Has.Count.EqualTo(acknowledgementsBeforeBob + 1));
            Assert.That(acknowledgements[^1].Success, Is.False);
            Assert.That(acknowledgements[^1].Error, Does.Contain("already in use"),
                "A second player must not race an upload already active on this table.");
        }
        finally
        {
            Settings.ImmersiveMode = false;
            service.Dispose();
            var worker = typeof(ServerCartographyService).GetField("worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            var completion = (Task)worker.GetType().GetProperty("Completion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(Path.GetDirectoryName(Path.GetFullPath(root)), Is.EqualTo(Path.TrimEndingDirectorySeparator(Path.GetTempPath())));
            Directory.Delete(root, true);
        }
    }
}
