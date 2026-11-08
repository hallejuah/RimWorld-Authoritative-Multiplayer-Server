using System.Buffers.Binary;
using Multiplayer.Authoritative.Transport;
using static Fixtures;

internal static class PacketTests
{
    private static WorldViewPacket[] Packets()
    {
        var p = Plan();
        return [new WorldViewHello(1, World, p.Offer.ContentSha256), p.Offer,
            new WorldViewChunkRequest(p.Offer.TransferId, 0), p.GetChunk(0),
            new WorldViewFinish(p.Offer.TransferId, p.Offer.PayloadSha256), new WorldViewComplete(p.Offer.TransferId)];
    }

    public static async Task Roundtrip()
    {
        foreach (var packet in Packets())
        {
            var encoded = WorldViewPacketCodec.Encode(packet);
            using var stream = new MemoryStream(encoded);
            var copy = await WorldViewPacketCodec.ReadAsync(stream) ?? throw new Exception("packet lost");
            Check(encoded.SequenceEqual(WorldViewPacketCodec.Encode(copy)), "packet changed on roundtrip");
            Check(await WorldViewPacketCodec.ReadAsync(stream) is null, "clean EOF not recognized");
        }
    }

    public static async Task Fragmentation()
    {
        foreach (var packet in Packets())
        {
            var encoded = WorldViewPacketCodec.Encode(packet);
            using var fragmented = new OneByteReads(encoded);
            var copy = await WorldViewPacketCodec.ReadAsync(fragmented) ?? throw new Exception("fragmented packet lost");
            Check(encoded.SequenceEqual(WorldViewPacketCodec.Encode(copy)), "partial reads corrupted framing");
        }
        using var concatenated = new MemoryStream(Packets().SelectMany(WorldViewPacketCodec.Encode).ToArray());
        for (var i = 0; i < 6; i++) Check(await WorldViewPacketCodec.ReadAsync(concatenated) is not null, "coalesced packets lost");
        Check(await WorldViewPacketCodec.ReadAsync(concatenated) is null, "coalesced packet tail");
    }

    public static async Task Truncation()
    {
        foreach (var packet in Packets())
        {
            var bytes = WorldViewPacketCodec.Encode(packet);
            // All cut positions, including inside a chunk and its fixed metadata.
            for (var length = 1; length < bytes.Length; length++)
            {
                using var stream = new MemoryStream(bytes[..length]);
                await ThrowsAsync<EndOfStreamException>(async () => { await WorldViewPacketCodec.ReadAsync(stream); });
            }
        }
    }

    public static async Task BadHeaders()
    {
        var valid = WorldViewPacketCodec.Encode(Plan().Offer);
        foreach (var length in new uint[] { 0, uint.MaxValue, (uint)WorldViewPacketCodec.MaxFrameBytes, 5000 })
        {
            var header = valid[..9]; BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), length);
            using var stream = new MemoryStream(header);
            await ThrowsAsync<InvalidDataException>(async () => { await WorldViewPacketCodec.ReadAsync(stream); });
        }
        foreach (var index in new[] { 0, 1, 2, 3, 8 })
        {
            var header = valid[..9]; header[index] = 255;
            using var stream = new MemoryStream(header);
            await ThrowsAsync<InvalidDataException>(async () => { await WorldViewPacketCodec.ReadAsync(stream); });
        }
        var oldV1 = new byte[] { 0, 0, 0, 5, 1, 1, 1, 1, 1 };
        using var legacy = new MemoryStream(oldV1);
        await ThrowsAsync<InvalidDataException>(async () => { await WorldViewPacketCodec.ReadAsync(legacy); });
    }

    public static async Task BadScalarsAndCancellation()
    {
        var p = Plan();
        Throws<InvalidDataException>(() => WorldViewPacketCodec.Encode(new WorldViewHello(2, World, p.Offer.ContentSha256)));
        Throws<InvalidDataException>(() => WorldViewPacketCodec.Encode(new WorldViewChunkRequest(Guid.Empty, 0)));
        Throws<InvalidDataException>(() => WorldViewPacketCodec.Encode(new WorldViewChunkRequest(Guid.NewGuid(), -1)));
        Throws<InvalidDataException>(() => WorldViewPacketCodec.Encode(p.GetChunk(0) with { Payload = new byte[65537] }));
        Throws<InvalidDataException>(() => WorldViewPacketCodec.Encode(p.GetChunk(0) with { Sha256 = "bad" }));
        var bytes = WorldViewPacketCodec.Encode(p.Offer);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(9), 99);
        using var stream = new MemoryStream(bytes);
        await ThrowsAsync<InvalidDataException>(async () => { await WorldViewPacketCodec.ReadAsync(stream); });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var empty = new MemoryStream();
        await ThrowsAsync<OperationCanceledException>(async () => { await WorldViewPacketCodec.ReadAsync(empty, cancelled.Token); });
        await ThrowsAsync<OperationCanceledException>(() => WorldViewPacketCodec.WriteAsync(empty, p.Offer, cancelled.Token));
    }

    private sealed class OneByteReads : MemoryStream
    {
        public OneByteReads(byte[] bytes) : base(bytes, false) { }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            base.ReadAsync(buffer, offset, Math.Min(count, 1), token);
    }
}
