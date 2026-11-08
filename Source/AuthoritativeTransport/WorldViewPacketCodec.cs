using System.Text;

namespace Multiplayer.Authoritative.Transport;

// Header: ASCII RWVT, big-endian u32 length INCLUDING kind, kind:u8.
// Body uses little-endian integers, Guid.ToByteArray(), raw SHA-256 bytes.
// All lengths are type-bounded BEFORE allocation; no JSON or arbitrary strings.
public static class WorldViewPacketCodec
{
    public const int MaxFrameBytes = 9 + 16 + 4 + 32 + WorldViewTransferFormat.MaxChunkBytes;

    public static byte[] Encode(WorldViewPacket packet)
    {
        if (packet is null) throw new ArgumentNullException(nameof(packet));
        using var body = new MemoryStream();
        using var writer = new BinaryWriter(body, Encoding.UTF8, true);
        byte kind;
        switch (packet)
        {
            case WorldViewHello p:
                if (p.Version != WorldViewTransferFormat.Version || p.WorldId == Guid.Empty)
                    throw new InvalidDataException("Unsupported checkpoint hello");
                kind = 1;
                writer.Write(p.Version); Id(p.WorldId); Hash(p.ContentSha256);
                break;
            case WorldViewOffer p:
                try { WorldViewTransferValidation.ValidateOffer(p); }
                catch (ArgumentException e) { throw new InvalidDataException("Invalid checkpoint offer", e); }
                kind = 2;
                writer.Write(p.Version); writer.Write(p.BootstrapVersion); writer.Write(p.GraphVersion);
                Id(p.TransferId); Id(p.WorldId); writer.Write(p.NetworkVersion); writer.Write(p.GameStateVersion);
                writer.Write(p.BootstrapBytes); writer.Write(p.GraphBytes); writer.Write(p.ChunkBytes);
                Hash(p.ContentSha256); Hash(p.PayloadSha256);
                break;
            case WorldViewChunkRequest p:
                kind = 3;
                Id(p.TransferId); Index(p.Index);
                break;
            case WorldViewChunk p:
                if (p.Payload is null || p.Payload.Length == 0 || p.Payload.Length > WorldViewTransferFormat.MaxChunkBytes)
                    throw new InvalidDataException("Invalid chunk payload size");
                kind = 4;
                Id(p.TransferId); Index(p.Index); Hash(p.Sha256); writer.Write(p.Payload);
                break;
            case WorldViewFinish p:
                kind = 5;
                Id(p.TransferId); Hash(p.PayloadSha256);
                break;
            case WorldViewComplete p:
                kind = 6;
                Id(p.TransferId);
                break;
            default: throw new InvalidDataException("Unknown checkpoint packet");
        }
        writer.Flush();
        var payload = body.ToArray();
        ValidateLength(kind, payload.Length);
        var length = payload.Length + 1;
        var frame = new byte[9 + payload.Length];
        frame[0] = (byte)'R'; frame[1] = (byte)'W'; frame[2] = (byte)'V'; frame[3] = (byte)'T';
        frame[4] = (byte)(length >> 24); frame[5] = (byte)(length >> 16);
        frame[6] = (byte)(length >> 8); frame[7] = (byte)length; frame[8] = kind;
        Buffer.BlockCopy(payload, 0, frame, 9, payload.Length);
        return frame;

        void Id(Guid id)
        {
            if (id == Guid.Empty) throw new InvalidDataException("Empty checkpoint identity");
            writer.Write(id.ToByteArray());
        }
        void Hash(string hex) => writer.Write(WorldViewTransferValidation.DigestBytes(hex));
        void Index(int index)
        {
            if (index < 0 || index >= WorldViewTransferFormat.MaxChunks)
                throw new InvalidDataException("Invalid checkpoint chunk index");
            writer.Write(index);
        }
    }

    public static async Task WriteAsync(Stream stream, WorldViewPacket packet, CancellationToken cancellationToken = default)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Encode(packet);
        await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // Clean EOF before a header returns null. EOF within a frame always throws.
    public static async Task<WorldViewPacket?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        cancellationToken.ThrowIfCancellationRequested();
        var header = new byte[9];
        if (await stream.ReadAsync(header, 0, 1, cancellationToken).ConfigureAwait(false) == 0) return null;
        await ReadFully(stream, header, 1, 8, cancellationToken).ConfigureAwait(false);
        if (header[0] != 'R' || header[1] != 'W' || header[2] != 'V' || header[3] != 'T')
            throw new InvalidDataException("Not a checkpoint transfer frame");
        var length = ((long)header[4] << 24) | ((long)header[5] << 16) | ((long)header[6] << 8) | header[7];
        if (length < 1 || length > MaxFrameBytes - 8)
            throw new InvalidDataException("Checkpoint frame size exceeds budget");
        var count = (int)length - 1;
        var kind = header[8];
        ValidateLength(kind, count);
        var payload = new byte[count];
        await ReadFully(stream, payload, 0, count, cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream(payload, false);
        using var reader = new BinaryReader(memory, Encoding.UTF8);
        WorldViewPacket result = kind switch
        {
            1 => new WorldViewHello(reader.ReadInt32(), Id(), Hash()),
            2 => new WorldViewOffer(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), Id(), Id(),
                reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), Hash(), Hash()),
            3 => new WorldViewChunkRequest(Id(), reader.ReadInt32()),
            4 => ReadChunk(),
            5 => new WorldViewFinish(Id(), Hash()),
            6 => new WorldViewComplete(Id()),
            _ => throw new InvalidDataException("Unknown checkpoint kind")
        };
        if (memory.Position != memory.Length) throw new InvalidDataException("Trailing checkpoint packet data");
        // Reuse strict scalar validation. At most one 64 KiB frame is encoded;
        // checksum verification of chunk contents belongs to the assembler.
        Encode(result);
        return result;

        Guid Id() => new(reader.ReadBytes(16));
        string Hash() => WorldViewTransferValidation.Hex(reader.ReadBytes(32));
        WorldViewChunk ReadChunk()
        {
            var id = Id();
            var index = reader.ReadInt32();
            var hash = Hash();
            return new(id, index, reader.ReadBytes(checked((int)(memory.Length - memory.Position))), hash);
        }
    }

    private static void ValidateLength(byte kind, int length)
    {
        var valid = kind switch
        {
            1 => length == 52,
            2 => length == 136,
            3 => length == 20,
            4 => length >= 53 && length <= 52 + WorldViewTransferFormat.MaxChunkBytes,
            5 => length == 48,
            6 => length == 16,
            _ => false
        };
        if (!valid) throw new InvalidDataException("Invalid checkpoint packet kind or type-specific size");
    }

    private static async Task ReadFully(Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
    {
        while (count != 0)
        {
            token.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer, offset, count, token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Truncated checkpoint frame");
            offset += read;
            count -= read;
        }
    }
}
