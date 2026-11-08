using System.Net;
using System.Net.Sockets;
using Multiplayer.Authoritative;

namespace Multiplayer.Authoritative.Transport;

// Read-only LOCAL test transport. Takes ownership of an already connected socket
// and closes it on success, failure or cancellation. Both endpoints must be
// loopback. No listener, credentials, remote authentication, TLS or gameplay here.
public static class LoopbackWorldViewTransfer
{
    public static async Task SendAsync(TcpClient peer, PreparedWorldViewTransfer transfer,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (peer is null) throw new ArgumentNullException(nameof(peer));
        using (peer)
        {
            CheckPeerAndTimeout(peer, timeout);
            if (transfer is null) throw new ArgumentNullException(nameof(transfer));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            // Framework/Mono streams need socket closure to reliably interrupt
            // pending async I/O. ReadTimeout does not govern ReadAsync.
            using var registration = deadline.Token.Register(() => peer.Close());
            try
            {
                var stream = peer.GetStream();
                var token = deadline.Token;
                var hello = await Required(stream, token).ConfigureAwait(false) as WorldViewHello;
                if (hello is null || hello.Version != WorldViewTransferFormat.Version ||
                    hello.WorldId != transfer.Offer.WorldId || hello.ContentSha256 != transfer.Offer.ContentSha256)
                    throw new InvalidDataException("Checkpoint hello rejected before world data delivery");
                await WorldViewPacketCodec.WriteAsync(stream, transfer.Offer, token).ConfigureAwait(false);
                // One request / one reply, bounded total work. No full-checkpoint
                // output queue and no unbounded ACK bitmap supplied by a client.
                var replies = 0;
                while (true)
                {
                    var packet = await Required(stream, token).ConfigureAwait(false);
                    if (packet is WorldViewFinish finish)
                    {
                        if (finish.TransferId != transfer.Offer.TransferId || finish.PayloadSha256 != transfer.Offer.PayloadSha256)
                            throw new InvalidDataException("Invalid checkpoint completion receipt");
                        await WorldViewPacketCodec.WriteAsync(stream, new WorldViewComplete(finish.TransferId), token)
                            .ConfigureAwait(false);
                        return;
                    }
                    if (packet is not WorldViewChunkRequest request || request.TransferId != transfer.Offer.TransferId ||
                        request.Index < 0 || request.Index >= transfer.ChunkCount || ++replies > transfer.ChunkCount + 8)
                        throw new InvalidDataException("Invalid or excessive checkpoint requests");
                    await WorldViewPacketCodec.WriteAsync(stream, transfer.GetChunk(request.Index), token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (deadline.IsCancellationRequested)
            {
                throw new OperationCanceledException("Checkpoint send cancelled or timed out", e, deadline.Token);
            }
        }
    }

    // Preserve assembler after a dropped connection, then call again with a new
    // connected loopback socket and the SAME assembler. The server must retain
    // its PreparedWorldViewTransfer to reuse verified chunks. No automatic retry
    // loop is hidden here. Do not share an assembler between simultaneous sessions.
    public static async Task<WorldViewCheckpoint> ReceiveAsync(TcpClient peer, WorldViewTransferAssembler assembler,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (peer is null) throw new ArgumentNullException(nameof(peer));
        using (peer)
        {
            CheckPeerAndTimeout(peer, timeout);
            if (assembler is null) throw new ArgumentNullException(nameof(assembler));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            using var registration = deadline.Token.Register(() => peer.Close());
            try
            {
                var stream = peer.GetStream();
                var token = deadline.Token;
                await WorldViewPacketCodec.WriteAsync(stream,
                    new WorldViewHello(WorldViewTransferFormat.Version, assembler.WorldId, assembler.ContentSha256), token)
                    .ConfigureAwait(false);
                var offer = await Required(stream, token).ConfigureAwait(false) as WorldViewOffer
                    ?? throw new InvalidDataException("Expected checkpoint offer");
                var admission = assembler.Begin(offer);
                if (admission is not (WorldViewOfferResult.Started or WorldViewOfferResult.Resumed))
                    throw new InvalidDataException("Checkpoint offer rejected: " + admission);
                // Window one provides deliberate backpressure. Out-of-order
                // packets are supported by the assembler, not solicited by TCP.
                var replies = 0;
                while (true)
                {
                    var missing = assembler.GetMissingIndices(1);
                    if (missing.Length == 0) break;
                    if (++replies > WorldViewTransferValidation.ValidateOffer(offer))
                        throw new InvalidDataException("Checkpoint session made no bounded progress");
                    await WorldViewPacketCodec.WriteAsync(stream,
                        new WorldViewChunkRequest(offer.TransferId, missing[0]), token).ConfigureAwait(false);
                    var chunk = await Required(stream, token).ConfigureAwait(false) as WorldViewChunk;
                    if (chunk is null || chunk.Index != missing[0] ||
                        assembler.Accept(chunk) is not (WorldViewChunkResult.Accepted or WorldViewChunkResult.Duplicate))
                        throw new InvalidDataException("Invalid checkpoint chunk reply");
                }
                token.ThrowIfCancellationRequested();
                if (assembler.Offer != offer) throw new InvalidDataException("Transfer superseded while receiving");
                var candidate = assembler.GetValidatedCheckpoint();
                await WorldViewPacketCodec.WriteAsync(stream,
                    new WorldViewFinish(offer.TransferId, offer.PayloadSha256), token).ConfigureAwait(false);
                var complete = await Required(stream, token).ConfigureAwait(false) as WorldViewComplete;
                if (complete?.TransferId != offer.TransferId)
                    throw new InvalidDataException("Missing checkpoint completion acknowledgement");
                token.ThrowIfCancellationRequested();
                // Caller installs on its presentation thread using the existing
                // ClientWorldViewReplica. Socket workers never touch that replica.
                return candidate;
            }
            catch (Exception e) when (deadline.IsCancellationRequested)
            {
                throw new OperationCanceledException("Checkpoint receive cancelled or timed out", e, deadline.Token);
            }
        }
    }

    private static async Task<WorldViewPacket> Required(Stream stream, CancellationToken token) =>
        await WorldViewPacketCodec.ReadAsync(stream, token).ConfigureAwait(false)
        ?? throw new EndOfStreamException("Checkpoint connection closed before completion");

    private static void CheckPeerAndTimeout(TcpClient peer, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (peer.Client.LocalEndPoint is not IPEndPoint local || peer.Client.RemoteEndPoint is not IPEndPoint remote ||
            !IPAddress.IsLoopback(local.Address) || !IPAddress.IsLoopback(remote.Address))
            throw new InvalidOperationException("Checkpoint transfer is restricted to connected loopback sockets");
    }
}
