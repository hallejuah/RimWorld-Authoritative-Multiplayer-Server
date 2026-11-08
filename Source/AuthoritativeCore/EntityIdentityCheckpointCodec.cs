using System.Security.Cryptography;
using System.Text;

namespace Multiplayer.Authoritative;

// Independent, bounded server-save metadata format; NOT an Amity wire format.
// Little endian: RWID, version:i32, Guid.ToByteArray():16, next:i64, count:i32;
// records: id:i64, retired:u8, UTF8 length:u16, UTF8 bytes; then SHA256:32.
// SHA-256 detects accidental corruption; it is NOT authentication or durability.
public static class EntityIdentityCheckpointCodec
{
    private const uint Magic = 0x44495752;
    private const int HeaderBytes = 36;
    private const int DigestBytes = 32;

    public static byte[] Encode(EntityIdentityCheckpoint checkpoint)
    {
        if (checkpoint is null) throw new ArgumentNullException(nameof(checkpoint));
        var clean = ServerEntityRegistry.Restore(checkpoint, checkpoint.WorldId).CaptureCheckpoint();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, EntityIdentityFormat.Utf8, true))
        {
            writer.Write(Magic);
            writer.Write(EntityIdentityFormat.Version);
            writer.Write(clean.WorldId.ToByteArray());
            writer.Write(clean.NextEntityId);
            writer.Write(clean.Entries.Count);
            foreach (var entry in clean.Entries)
            {
                var key = EntityIdentityFormat.Utf8.GetBytes(entry.SourceKey);
                writer.Write(entry.EntityId);
                writer.Write((byte)(entry.Retired ? 1 : 0));
                writer.Write(checked((ushort)key.Length));
                writer.Write(key);
            }
        }
        if (stream.Length > EntityIdentityFormat.MaxBytes - DigestBytes)
            throw new InvalidDataException("Identity checkpoint exceeds its byte budget");
        var body = stream.ToArray();
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(body);
        stream.Write(digest, 0, digest.Length);
        return stream.ToArray();
    }

    public static EntityIdentityCheckpoint Decode(byte[] bytes, Guid expectedWorldId)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        if (expectedWorldId == Guid.Empty || bytes.Length < HeaderBytes + DigestBytes ||
            bytes.Length > EntityIdentityFormat.MaxBytes)
            throw new InvalidDataException("Invalid identity checkpoint size or expected world");
        // Parse the same immutable byte copy whose digest was verified.
        var input = (byte[])bytes.Clone();
        var bodyLength = input.Length - DigestBytes;
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(input, 0, bodyLength);
        var mismatch = 0;
        for (var i = 0; i < DigestBytes; i++) mismatch |= digest[i] ^ input[bodyLength + i];
        if (mismatch != 0) throw new InvalidDataException("Identity checkpoint checksum mismatch");

        try
        {
            using var stream = new MemoryStream(input, 0, bodyLength, false);
            using var reader = new BinaryReader(stream, EntityIdentityFormat.Utf8);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != EntityIdentityFormat.Version)
                throw new InvalidDataException("Unsupported identity checkpoint format");
            var world = new Guid(reader.ReadBytes(16));
            var next = reader.ReadInt64();
            var count = reader.ReadInt32();
            if (world != expectedWorldId || next <= 0 || count < 0 || count > EntityIdentityFormat.MaxEntities ||
                count > (bodyLength - HeaderBytes) / 12)
                throw new InvalidDataException("Invalid or foreign identity checkpoint header");
            var entries = new EntityIdentityEntry[count];
            for (var i = 0; i < count; i++)
            {
                var id = reader.ReadInt64();
                var retired = reader.ReadByte();
                var length = reader.ReadUInt16();
                if (retired > 1 || length == 0 || length > EntityIdentityFormat.MaxSourceKeyBytes ||
                    length > stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid identity checkpoint record");
                var key = EntityIdentityFormat.Utf8.GetString(reader.ReadBytes(length));
                entries[i] = new EntityIdentityEntry(id, key, retired == 1);
            }
            if (stream.Position != stream.Length)
                throw new InvalidDataException("Identity checkpoint has trailing data");
            var candidate = new EntityIdentityCheckpoint(EntityIdentityFormat.Version, world, next, entries);
            return ServerEntityRegistry.Restore(candidate, expectedWorldId).CaptureCheckpoint();
        }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated identity checkpoint", e); }
        catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid identity checkpoint UTF-8", e); }
    }
}
