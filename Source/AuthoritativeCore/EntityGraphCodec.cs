using System.Security.Cryptography;
using System.Text;

namespace Multiplayer.Authoritative;

// Own binary format, unrelated to Amity and independent of v1 TCP / bootstrap v2.
// SHA-256 detects corruption only; it does NOT authenticate the sending server.
public static class EntityGraphCodec
{
    private const uint Magic = 0x47455752; // RWEG, little endian
    private const int DigestBytes = 32;

    public static byte[] Encode(EntityGraphSnapshot source)
    {
        var graph = EntityGraphValidator.ValidateAndCopy(source);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, EntityGraphValidator.Utf8, true))
        {
            writer.Write(Magic);
            writer.Write(graph.FormatVersion);
            writer.Write(graph.WorldId.ToByteArray());
            writer.Write(graph.NetworkVersion);
            writer.Write(graph.GameStateVersion);
            writer.Write(graph.Maps.Count);
            foreach (var map in graph.Maps)
            {
                writer.Write(map.DomainId);
                writer.Write(map.Width);
                writer.Write(map.Height);
            }
            writer.Write(graph.Nodes.Count);
            foreach (var node in graph.Nodes)
            {
                var e = node.Entity;
                writer.Write(e.Id);
                writer.Write(e.DomainId);
                writer.Write(e.FactionId);
                WriteName(writer, e.DefName);
                writer.Write(e.X);
                writer.Write(e.Z);
                writer.Write(e.Rotation);
                writer.Write(e.StackCount);
                writer.Write((byte)e.Kind);
                if (e.Pawn is { } p)
                {
                    writer.Write((byte)((p.Drafted ? 1 : 0) | (p.Downed ? 2 : 0) | (p.Dead ? 4 : 0)));
                    WriteName(writer, p.CurrentJobDef);
                }
                writer.Write(node.Attachment.HolderEntityId);
                writer.Write((byte)node.Attachment.Slot);
                CheckBudget(stream);
            }
            writer.Write(graph.Holders.Count);
            foreach (var holder in graph.Holders)
            {
                writer.Write(holder.OwnerEntityId);
                writer.Write((byte)holder.Slot);
                CheckBudget(stream);
            }
        }
        CheckBudget(stream);
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(stream.ToArray());
        stream.Write(digest, 0, digest.Length);
        return stream.ToArray();
    }

    public static EntityGraphSnapshot Decode(byte[] bytes, Guid expectedWorldId)
    {
        // Fixed header, >=1 map, two collection counts and checksum.
        if (bytes is null || bytes.Length < 96 || bytes.Length > EntityGraphFormat.MaxBytes ||
            expectedWorldId == Guid.Empty)
            throw new InvalidDataException("Invalid graph size or expected world");
        var input = (byte[])bytes.Clone();
        var bodyLength = input.Length - DigestBytes;
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(input, 0, bodyLength);
        var mismatch = 0;
        for (var i = 0; i < DigestBytes; i++) mismatch |= digest[i] ^ input[bodyLength + i];
        if (mismatch != 0) throw new InvalidDataException("Entity graph checksum mismatch");
        try
        {
            using var stream = new MemoryStream(input, 0, bodyLength, false);
            using var reader = new BinaryReader(stream, EntityGraphValidator.Utf8);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != EntityGraphFormat.Version)
                throw new InvalidDataException("Unsupported entity graph format");
            var worldId = new Guid(reader.ReadBytes(16));
            if (worldId != expectedWorldId) throw new InvalidDataException("Foreign entity graph world");
            var network = reader.ReadInt64();
            var game = reader.ReadInt64();
            var maps = new EntityGraphMap[ReadCount(reader, WorldBootstrapFormat.MaxMaps, 12)];
            for (var i = 0; i < maps.Length; i++)
                maps[i] = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            var nodes = new EntityGraphNode[ReadCount(reader, EntityGraphFormat.MaxEntities, 45)];
            for (var i = 0; i < nodes.Length; i++)
            {
                var id = reader.ReadInt64();
                var domain = reader.ReadInt32();
                var faction = reader.ReadInt32();
                var def = ReadName(reader, false)!;
                var x = reader.ReadInt32();
                var z = reader.ReadInt32();
                var rotation = reader.ReadInt32();
                var stack = reader.ReadInt32();
                var kind = (PresentationKind)reader.ReadByte();
                if (kind is not (PresentationKind.Thing or PresentationKind.Pawn))
                    throw new InvalidDataException("Unknown graph entity kind");
                PawnPresentation? pawn = null;
                if (kind == PresentationKind.Pawn)
                {
                    var flags = reader.ReadByte();
                    if (flags > 7) throw new InvalidDataException("Unknown pawn flag bits");
                    pawn = new((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, ReadName(reader, true));
                }
                var attachment = new EntityAttachment(reader.ReadInt64(), (EntityHolderSlot)reader.ReadByte());
                nodes[i] = new(new(id, domain, faction, def, x, z, rotation, stack, kind, pawn), attachment);
            }
            var holders = new EntityHolder[ReadCount(reader, EntityGraphFormat.MaxHolders, 9)];
            for (var i = 0; i < holders.Length; i++)
                holders[i] = new(reader.ReadInt64(), (EntityHolderSlot)reader.ReadByte());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing entity graph data");
            return EntityGraphValidator.ValidateAndCopy(new(EntityGraphFormat.Version, worldId, network, game,
                maps, nodes, holders));
        }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated entity graph", e); }
        catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid graph UTF-8", e); }
        catch (ArgumentException e) { throw new InvalidDataException("Invalid entity graph structure", e); }
    }

    private static void WriteName(BinaryWriter writer, string? name)
    {
        var bytes = name is null ? Array.Empty<byte>() : EntityGraphValidator.Utf8.GetBytes(name);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }

    private static string? ReadName(BinaryReader reader, bool nullable)
    {
        var length = reader.ReadUInt16();
        if (length == 0 && nullable) return null;
        if (length == 0 || length > EntityGraphFormat.MaxNameBytes ||
            length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid graph name length");
        return EntityGraphValidator.Utf8.GetString(reader.ReadBytes(length));
    }

    private static int ReadCount(BinaryReader reader, int maximum, int minimumRecordBytes)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum ||
            count > (reader.BaseStream.Length - reader.BaseStream.Position) / minimumRecordBytes)
            throw new InvalidDataException("Invalid graph collection count");
        return count;
    }

    private static void CheckBudget(MemoryStream stream)
    {
        if (stream.Length > EntityGraphFormat.MaxBytes - DigestBytes)
            throw new ArgumentException("Encoded graph exceeds byte budget", "source");
    }
}
