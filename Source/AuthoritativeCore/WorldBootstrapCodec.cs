using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplayer.Authoritative;

// v2 is an independent initial-state envelope. The v1 pawn-position stream
// remains the only transport currently hooked to the optional game probe.
public static class WorldBootstrapCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 24,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] Encode(WorldBootstrapV2 source)
    {
        var canonical = WorldBootstrapValidator.ValidateAndCopy(source);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, Options);
        CheckSize(bytes);
        return bytes;
    }

    public static WorldBootstrapV2 Decode(byte[] bytes)
    {
        CheckSize(bytes);
        WorldBootstrapV2? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<WorldBootstrapV2>(bytes, Options);
            return WorldBootstrapValidator.ValidateAndCopy(parsed ??
                throw new JsonException("Null v2 bootstrap"));
        }
        catch (ArgumentException e)
        {
            throw new JsonException("Invalid v2 bootstrap state", e);
        }
    }

    // Integrity fingerprint only. NOT an authenticated signature and cannot
    // prove server identity; TLS/authenticated session is separate work.
    public static string ComputeSha256(WorldBootstrapV2 source)
    {
        var encoded = Encode(source);
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(encoded);
        return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
    }

    private static void CheckSize(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 ||
            bytes.Length > WorldBootstrapFormat.MaxBytes)
            throw new ArgumentException("Invalid v2 snapshot size", nameof(bytes));
    }
}
