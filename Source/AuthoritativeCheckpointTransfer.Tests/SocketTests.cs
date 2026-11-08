using System.Net;
using System.Net.Sockets;
using Multiplayer.Authoritative;
using Multiplayer.Authoritative.Transport;
using static Fixtures;

internal static class SocketTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private static async Task<(TcpClient Server, TcpClient Client)> Connect()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient { NoDelay = true };
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await accept; server.NoDelay = true;
            return (server, client);
        }
        catch { client.Dispose(); throw; }
        finally { listener.Stop(); }
    }

    public static async Task Roundtrip()
    {
        var p = Plan(); var r = Assembler(); var sockets = await Connect();
        var send = LoopbackWorldViewTransfer.SendAsync(sockets.Server, p, Budget);
        var result = await LoopbackWorldViewTransfer.ReceiveAsync(sockets.Client, r, Budget);
        await send;
        Same(View(), result);
        // Both creation and install below run on the current presentation thread.
        var replica = new ClientWorldViewReplica(Manifest, World);
        Check(replica.TryInstall(result) == WorldViewInstallResult.Accepted, "received pair not installable");
    }

    public static async Task Reconnect()
    {
        var p = Plan(); var r = Assembler(); var first = await Connect(); var indices = new List<int>();
        var send = ManualServer(first.Server, p, indices, dropAfter: 2);
        await ThrowsAsync<IOException>(async () => { await LoopbackWorldViewTransfer.ReceiveAsync(first.Client, r, Budget); });
        await send;
        Check(r.ReceivedCount == 2 && indices.SequenceEqual(new[] { 0, 1 }), "disconnect did not retain two complete chunks");
        var second = await Connect(); indices.Clear();
        send = ManualServer(second.Server, p, indices);
        var result = await LoopbackWorldViewTransfer.ReceiveAsync(second.Client, r, Budget);
        await send;
        Check(indices.SequenceEqual(Enumerable.Range(2, p.ChunkCount - 2)), "reconnect re-requested already verified chunks");
        Same(View(), result);
    }

    public static async Task LostCompletion()
    {
        var p = Plan(); var r = Assembler(); var first = await Connect();
        var send = ManualServer(first.Server, p, new(), dropCompletion: true);
        await ThrowsAsync<IOException>(async () => { await LoopbackWorldViewTransfer.ReceiveAsync(first.Client, r, Budget); });
        await send;
        Check(r.ReceivedCount == p.ChunkCount, "lost completion acknowledgement destroyed validated chunks");
        var second = await Connect(); var indices = new List<int>();
        send = ManualServer(second.Server, p, indices);
        var result = await LoopbackWorldViewTransfer.ReceiveAsync(second.Client, r, Budget);
        await send;
        Check(indices.Count == 0, "lost completion forced payload retransmission");
        Same(View(), result);
    }

    public static async Task NewCheckpointAfterReconnect()
    {
        var old = Plan(); var r = Assembler(); r.Begin(old.Offer); r.Accept(old.GetChunk(0));
        var newer = new PreparedWorldViewTransfer(View(11, 21), 4096); var sockets = await Connect();
        var send = LoopbackWorldViewTransfer.SendAsync(sockets.Server, newer, Budget);
        var result = await LoopbackWorldViewTransfer.ReceiveAsync(sockets.Client, r, Budget);
        await send;
        Check(r.Offer == newer.Offer && r.ReceivedCount == newer.ChunkCount, "replacement snapshot not fully transferred");
        Same(View(11, 21), result);
    }

    public static async Task AdmissionBeforeData()
    {
        var p = Plan();
        foreach (var hello in new[] { new WorldViewHello(1, Guid.NewGuid(), p.Offer.ContentSha256),
                     new WorldViewHello(1, World, new string('0', 64)) })
        {
            var sockets = await Connect(); using var client = sockets.Client;
            var send = LoopbackWorldViewTransfer.SendAsync(sockets.Server, p, Budget);
            await WorldViewPacketCodec.WriteAsync(client.GetStream(), hello);
            await ThrowsAsync<InvalidDataException>(() => send);
            Check(await WorldViewPacketCodec.ReadAsync(client.GetStream()) is null, "rejected hello received a world offer");
        }
    }

    public static async Task CorruptChunk()
    {
        var p = Plan(); var r = Assembler(); var sockets = await Connect();
        var send = ManualServer(sockets.Server, p, new(), dropAfter: 1, corrupt: true);
        await ThrowsAsync<InvalidDataException>(async () => { await LoopbackWorldViewTransfer.ReceiveAsync(sockets.Client, r, Budget); });
        await send;
        Check(r.ReceivedCount == 0, "corrupt TCP chunk was retained");
        var retry = await Connect();
        send = LoopbackWorldViewTransfer.SendAsync(retry.Server, p, Budget);
        Same(View(), await LoopbackWorldViewTransfer.ReceiveAsync(retry.Client, r, Budget));
        await send;
    }

    public static async Task InvalidRequests()
    {
        var p = Plan();
        WorldViewPacket[] bad = [new WorldViewChunkRequest(Guid.NewGuid(), 0),
            new WorldViewChunkRequest(p.Offer.TransferId, p.ChunkCount),
            new WorldViewFinish(p.Offer.TransferId, new string('0', 64))];
        foreach (var packet in bad)
        {
            var sockets = await Connect(); using var client = sockets.Client;
            var send = LoopbackWorldViewTransfer.SendAsync(sockets.Server, p, Budget);
            await Hello(client, p);
            await WorldViewPacketCodec.WriteAsync(client.GetStream(), packet);
            await ThrowsAsync<InvalidDataException>(() => send);
        }
    }

    public static async Task RequestBudget()
    {
        var p = Plan(); var sockets = await Connect(); using var client = sockets.Client;
        var send = LoopbackWorldViewTransfer.SendAsync(sockets.Server, p, Budget);
        await Hello(client, p);
        for (var i = 0; i < p.ChunkCount + 8; i++)
        {
            await WorldViewPacketCodec.WriteAsync(client.GetStream(), new WorldViewChunkRequest(p.Offer.TransferId, 0));
            Check(await WorldViewPacketCodec.ReadAsync(client.GetStream()) is WorldViewChunk, "valid bounded retry failed");
        }
        await WorldViewPacketCodec.WriteAsync(client.GetStream(), new WorldViewChunkRequest(p.Offer.TransferId, 0));
        await ThrowsAsync<InvalidDataException>(() => send);
    }

    public static async Task Deadlines()
    {
        var p = Plan(); var first = await Connect(); using var idleClient = first.Client;
        await ThrowsAsync<OperationCanceledException>(() => LoopbackWorldViewTransfer.SendAsync(first.Server, p, TimeSpan.FromMilliseconds(150)));
        var second = await Connect(); using var idleServer = second.Server;
        var r = Assembler();
        await ThrowsAsync<OperationCanceledException>(async () =>
            { await LoopbackWorldViewTransfer.ReceiveAsync(second.Client, r, TimeSpan.FromMilliseconds(150)); });
        Check(r.BufferedBytes == 0, "timeout before offer allocated staging");
    }

    public static async Task CancellationPreservesProgress()
    {
        var p = Plan(); var r = Assembler(); r.Begin(p.Offer); r.Accept(p.GetChunk(0));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var sockets = await Connect(); using var server = sockets.Server;
        await ThrowsAsync<OperationCanceledException>(async () =>
            { await LoopbackWorldViewTransfer.ReceiveAsync(sockets.Client, r, Budget, cancellation.Token); });
        Check(r.ReceivedCount == 1, "cancelled connection discarded resumable data");
        r.DiscardBuffers(); Check(r.BufferedBytes == 0, "explicit release after cancellation failed");
    }

    public static async Task EndpointAndTimeoutGuards()
    {
        await ThrowsAsync<InvalidOperationException>(() => LoopbackWorldViewTransfer.SendAsync(new TcpClient(), Plan(), Budget));
        var sockets = await Connect(); using var client = sockets.Client;
        await ThrowsAsync<ArgumentOutOfRangeException>(() => LoopbackWorldViewTransfer.SendAsync(sockets.Server, Plan(), TimeSpan.Zero));
    }

    private static async Task Hello(TcpClient client, PreparedWorldViewTransfer plan)
    {
        await WorldViewPacketCodec.WriteAsync(client.GetStream(), new WorldViewHello(1, World, plan.Offer.ContentSha256));
        Check(await WorldViewPacketCodec.ReadAsync(client.GetStream()) is WorldViewOffer offer && offer == plan.Offer, "server offer");
    }

    // Fault-injection peer built from the real codec and real TCP sockets.
    private static async Task ManualServer(TcpClient peer, PreparedWorldViewTransfer plan, List<int> indices,
        int? dropAfter = null, bool dropCompletion = false, bool corrupt = false)
    {
        using (peer)
        using (var deadline = new CancellationTokenSource(Budget))
        using (deadline.Token.Register(() => peer.Close()))
        {
            var stream = peer.GetStream();
            Check(await WorldViewPacketCodec.ReadAsync(stream, deadline.Token) is WorldViewHello, "missing client hello");
            await WorldViewPacketCodec.WriteAsync(stream, plan.Offer, deadline.Token);
            while (true)
            {
                var packet = await WorldViewPacketCodec.ReadAsync(stream, deadline.Token);
                if (packet is WorldViewFinish finish)
                {
                    Check(finish.TransferId == plan.Offer.TransferId && finish.PayloadSha256 == plan.Offer.PayloadSha256, "wrong completion receipt");
                    if (!dropCompletion) await WorldViewPacketCodec.WriteAsync(stream, new WorldViewComplete(finish.TransferId), deadline.Token);
                    return;
                }
                var request = packet as WorldViewChunkRequest ?? throw new Exception("expected chunk request");
                indices.Add(request.Index);
                var chunk = plan.GetChunk(request.Index);
                if (corrupt) chunk.Payload[0] ^= 1;
                await WorldViewPacketCodec.WriteAsync(stream, chunk, deadline.Token);
                if (dropAfter == indices.Count) return;
            }
        }
    }
}
