using System.IO;
using System.Net;
using System.Net.Sockets;
using Multiplayer.Authoritative;

namespace Multiplayer.Authoritative.Transport;

// DTO-only client. It never calls Verse gameplay code or creates RimWorld
// Things/Maps. Not thread-safe: command and poll operations must be serialized.
public sealed class LoopbackTransportClient : IDisposable
{
    private readonly TcpClient socket;
    private readonly NetworkStream stream;
    public ClientStateReplica Replica { get; } = new();

    private LoopbackTransportClient(TcpClient socket)
    {
        this.socket = socket;
        stream = socket.GetStream();
    }

    public static async Task<LoopbackTransportClient> ConnectAsync(int port, string credential)
    {
        if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrEmpty(credential)) throw new ArgumentException("Missing credential", nameof(credential));

        var socket = new TcpClient();
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
            var client = new LoopbackTransportClient(socket);
            await WireFrameCodec.WriteAsync(client.stream, WireFrameKind.Hello,
                WireFrameCodec.Serialize(new WireHello(AuthoritativeProtocol.Version, credential)))
                .ConfigureAwait(false);
            var first = await WireFrameCodec.ReadAsync(client.stream).ConfigureAwait(false);
            if (first is null) throw new IOException("Connection closed during handshake");
            if (first.Kind == WireFrameKind.Error)
                throw new InvalidOperationException(WireFrameCodec.Deserialize<WireError>(first.Payload).Reason);
            if (first.Kind != WireFrameKind.Snapshot ||
                client.Replica.InstallSnapshot(ProtocolCodec.DecodeSnapshot(first.Payload)) !=
                    ReplicaApplyResult.Applied)
                throw new InvalidDataException("Invalid initial state");
            return client;
        }
        catch
        {
            socket.Close();
            throw;
        }
    }

    public async Task<CommandReceipt> SendCommandAsync(PlayerCommand command)
    {
        await WireFrameCodec.WriteAsync(stream, WireFrameKind.Command,
            ProtocolCodec.EncodeCommand(command)).ConfigureAwait(false);
        var frame = await ReadExpectedAsync(WireFrameKind.Receipt).ConfigureAwait(false);
        var receipt = WireFrameCodec.Deserialize<CommandReceipt>(frame.Payload);
        if (receipt.CommandId != command.CommandId)
            throw new InvalidDataException("Receipt command ID mismatch");
        return receipt;
    }

    public async Task<ReplicaApplyResult> PollAsync()
    {
        await WireFrameCodec.WriteAsync(stream, WireFrameKind.Poll,
            WireFrameCodec.Serialize(new WirePoll(Replica.Version))).ConfigureAwait(false);

        var result = ReplicaApplyResult.Applied;
        while (true)
        {
            var frame = await WireFrameCodec.ReadAsync(stream).ConfigureAwait(false) ??
                throw new EndOfStreamException("Poll ended without delimiter");
            if (frame.Kind == WireFrameKind.EndPoll)
                return result;
            if (frame.Kind == WireFrameKind.Error)
                throw new InvalidOperationException(WireFrameCodec.Deserialize<WireError>(frame.Payload).Reason);
            if (frame.Kind == WireFrameKind.Snapshot)
                result = Replica.InstallSnapshot(ProtocolCodec.DecodeSnapshot(frame.Payload));
            else if (frame.Kind == WireFrameKind.Delta)
                result = Replica.ApplyDelta(ProtocolCodec.DecodeDelta(frame.Payload));
            else
                throw new InvalidDataException("Unexpected state frame");
            if (result != ReplicaApplyResult.Applied)
                throw new InvalidDataException("Noncontiguous or invalid authoritative state");
        }
    }

    private async Task<WireFrame> ReadExpectedAsync(WireFrameKind expected)
    {
        var frame = await WireFrameCodec.ReadAsync(stream).ConfigureAwait(false) ??
            throw new EndOfStreamException("Server closed the connection");
        if (frame.Kind == WireFrameKind.Error)
            throw new InvalidOperationException(WireFrameCodec.Deserialize<WireError>(frame.Payload).Reason);
        if (frame.Kind != expected)
            throw new InvalidDataException("Unexpected reply frame");
        return frame;
    }

    public void Dispose()
    {
        stream.Dispose();
        socket.Close();
    }
}
