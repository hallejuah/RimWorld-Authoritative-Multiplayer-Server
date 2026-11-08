using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplayer.Authoritative;

// Typed, bounded DTOs only; never serialize the arbitrary CLR game object graph.
public static class ProtocolCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] EncodeCommand(PlayerCommand command)
    {
        if (command is null) throw new ArgumentNullException(nameof(command));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(command, Options);
        CheckSize(bytes, AuthoritativeProtocol.MaxCommandBytes);
        return bytes;
    }

    public static PlayerCommand DecodeCommand(byte[] bytes)
    {
        CheckSize(bytes, AuthoritativeProtocol.MaxCommandBytes);
        var command = JsonSerializer.Deserialize<PlayerCommand>(bytes, Options)
            ?? throw new JsonException("Null command");
        if (command.ProtocolVersion != AuthoritativeProtocol.Version ||
            command.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(command.SessionId))
            throw new JsonException("Invalid or incompatible command");
        return command;
    }

    public static byte[] EncodeSnapshot(WorldSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Options);
        CheckSize(bytes, AuthoritativeProtocol.MaxStateMessageBytes);
        return bytes;
    }

    public static WorldSnapshot DecodeSnapshot(byte[] bytes)
    {
        CheckSize(bytes, AuthoritativeProtocol.MaxStateMessageBytes);
        return JsonSerializer.Deserialize<WorldSnapshot>(bytes, Options)
            ?? throw new JsonException("Null snapshot");
    }

    public static byte[] EncodeDelta(WorldDelta delta)
    {
        if (delta is null) throw new ArgumentNullException(nameof(delta));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(delta, Options);
        CheckSize(bytes, AuthoritativeProtocol.MaxStateMessageBytes);
        return bytes;
    }

    public static WorldDelta DecodeDelta(byte[] bytes)
    {
        CheckSize(bytes, AuthoritativeProtocol.MaxStateMessageBytes);
        return JsonSerializer.Deserialize<WorldDelta>(bytes, Options)
            ?? throw new JsonException("Null delta");
    }

    private static void CheckSize(byte[]? bytes, int maximum)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length > maximum)
            throw new ArgumentException("Invalid network message size", nameof(bytes));
    }
}
