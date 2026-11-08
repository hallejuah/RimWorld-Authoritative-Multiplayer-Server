using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Multiplayer.Authoritative;

namespace Multiplayer.Authoritative.Transport;

// Local-only experimental protocol, entirely separate from lockstep protocol 54.
// Four-byte big-endian length INCLUDING the 1-byte kind, followed by kind,
// followed by bounded UTF-8 JSON or empty bytes. No TLS: LOOPBACK ONLY.
public enum WireFrameKind : byte
{
    Hello = 1,
    Snapshot = 2,
    Command = 3,
    Receipt = 4,
    Poll = 5,
    Delta = 6,
    EndPoll = 7,
    Error = 8
}

public sealed record WireFrame(WireFrameKind Kind, byte[] Payload);
public sealed record WireHello(int ProtocolVersion, string Credential);
public sealed record WirePoll(long AfterVersion);
public sealed record WireError(string Reason);

public static class WireFrameCodec
{
    public const int MaxHelloBytes = 1024;
    private const int MaxReceiptBytes = 4096;
    private const int MaxPollBytes = 128;

    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Json);

    public static T Deserialize<T>(byte[] bytes) =>
        JsonSerializer.Deserialize<T>(bytes, Json) ??
        throw new JsonException("Null wire payload");

    public static async Task WriteAsync(Stream stream, WireFrameKind kind, byte[] payload,
        CancellationToken cancellationToken = default)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        ValidateSize(kind, payload.Length);
        var size = checked(payload.Length + 1);
        var header = new byte[5]
        {
            (byte)(size >> 24), (byte)(size >> 16),
            (byte)(size >> 8), (byte)size, (byte)kind
        };
        await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
        if (payload.Length != 0)
            await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // A normal clean EOF before the header returns null. Truncation inside a
    // header or body is an error. Length is checked BEFORE payload allocation.
    public static async Task<WireFrame?> ReadAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        var header = new byte[4];
        var first = await stream.ReadAsync(header, 0, 1, cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await ReadFullyAsync(stream, header, 1, 3, cancellationToken).ConfigureAwait(false);
        var length = ((long)header[0] << 24) | ((long)header[1] << 16) |
                     ((long)header[2] << 8) | header[3];
        if (length < 1 || length > AuthoritativeProtocol.MaxStateMessageBytes + 1L)
            throw new InvalidDataException("Invalid frame length");

        var kindBuffer = new byte[1];
        await ReadFullyAsync(stream, kindBuffer, 0, 1, cancellationToken).ConfigureAwait(false);
        var kind = (WireFrameKind)kindBuffer[0];
        var payloadLength = checked((int)length - 1);
        ValidateSize(kind, payloadLength);
        var payload = new byte[payloadLength];
        if (payloadLength > 0)
            await ReadFullyAsync(stream, payload, 0, payloadLength, cancellationToken)
                .ConfigureAwait(false);
        return new WireFrame(kind, payload);
    }

    private static async Task ReadFullyAsync(Stream stream, byte[] buffer, int offset,
        int count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            var got = await stream.ReadAsync(buffer, offset, count, cancellationToken)
                .ConfigureAwait(false);
            if (got <= 0) throw new EndOfStreamException("Truncated wire frame");
            count -= got;
            offset += got;
        }
    }

    private static void ValidateSize(WireFrameKind kind, int length)
    {
        int limit = kind switch
        {
            WireFrameKind.Hello => MaxHelloBytes,
            WireFrameKind.Command => AuthoritativeProtocol.MaxCommandBytes,
            WireFrameKind.Poll => MaxPollBytes,
            WireFrameKind.Receipt => MaxReceiptBytes,
            WireFrameKind.Snapshot or WireFrameKind.Delta => AuthoritativeProtocol.MaxStateMessageBytes,
            WireFrameKind.Error => MaxHelloBytes,
            WireFrameKind.EndPoll => 0,
            _ => throw new InvalidDataException("Unknown wire frame type")
        };
        if (length > limit || length < 0 || (length == 0 && kind != WireFrameKind.EndPoll))
            throw new InvalidDataException("Frame exceeds its type-specific size limit");
    }
}
