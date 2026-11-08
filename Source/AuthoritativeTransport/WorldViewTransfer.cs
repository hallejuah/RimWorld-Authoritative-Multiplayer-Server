using System.Security.Cryptography;
using System.Text;
using Multiplayer.Authoritative;

namespace Multiplayer.Authoritative.Transport;

// An independent experimental checkpoint protocol. It does not change TCP v1,
// WorldBootstrapV2, graph v1, or the original lockstep protocol.
public static class WorldViewTransferFormat
{
    public const int Version = 1;
    public const int MinChunkBytes = 4096;
    public const int DefaultChunkBytes = 32 * 1024;
    public const int MaxChunkBytes = 64 * 1024;
    public const int MaxPayloadBytes = WorldBootstrapFormat.MaxBytes + EntityGraphFormat.MaxBytes;
    public const int MaxChunks = MaxPayloadBytes / MinChunkBytes;
}

public abstract record WorldViewPacket;
public sealed record WorldViewHello(int Version, Guid WorldId, string ContentSha256) : WorldViewPacket;
public sealed record WorldViewOffer(
    int Version, int BootstrapVersion, int GraphVersion,
    Guid TransferId, Guid WorldId, long NetworkVersion, long GameStateVersion,
    int BootstrapBytes, int GraphBytes, int ChunkBytes,
    string ContentSha256, string PayloadSha256) : WorldViewPacket;
public sealed record WorldViewChunkRequest(Guid TransferId, int Index) : WorldViewPacket;
public sealed record WorldViewChunk(Guid TransferId, int Index, byte[] Payload, string Sha256) : WorldViewPacket;
// Finish acknowledges a validated candidate, NOT a committed gameplay operation.
public sealed record WorldViewFinish(Guid TransferId, string PayloadSha256) : WorldViewPacket;
public sealed record WorldViewComplete(Guid TransferId) : WorldViewPacket;

public static class WorldViewTransferValidation
{
    public static int ValidateOffer(WorldViewOffer offer)
    {
        if (offer is null || offer.Version != WorldViewTransferFormat.Version ||
            offer.BootstrapVersion != WorldBootstrapFormat.Version || offer.GraphVersion != EntityGraphFormat.Version ||
            offer.TransferId == Guid.Empty || offer.WorldId == Guid.Empty ||
            offer.NetworkVersion < 0 || offer.GameStateVersion < 0 ||
            offer.BootstrapBytes <= 0 || offer.BootstrapBytes > WorldBootstrapFormat.MaxBytes ||
            offer.GraphBytes < 96 || offer.GraphBytes > EntityGraphFormat.MaxBytes ||
            offer.ChunkBytes < WorldViewTransferFormat.MinChunkBytes || offer.ChunkBytes > WorldViewTransferFormat.MaxChunkBytes ||
            !IsDigest(offer.ContentSha256) || !IsDigest(offer.PayloadSha256))
            throw new ArgumentException("Invalid checkpoint offer", nameof(offer));
        var total = checked(offer.BootstrapBytes + offer.GraphBytes);
        return checked((total + offer.ChunkBytes - 1) / offer.ChunkBytes);
    }

    // Versioned here by WorldViewTransferFormat.Version. Hash only the canonical
    // validated content manifest, preserving active mod order. Not authentication.
    public static string ContentDigest(ContentManifest source)
    {
        var manifest = WorldBootstrapValidator.ValidateManifest(source);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(WorldViewTransferFormat.Version);
            Write(manifest.RimWorldVersion);
            writer.Write(manifest.Mods.Count);
            foreach (var mod in manifest.Mods)
            {
                Write(mod.PackageId);
                Write(mod.Version);
                Write(mod.ContentSha256);
            }
            Write(manifest.DefCatalogSha256);
            void Write(string value)
            {
                var bytes = new UTF8Encoding(false, true).GetBytes(value);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }
        return Digest(stream.ToArray());
    }

    public static string Digest(byte[] bytes)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(bytes));
    }

    internal static bool IsDigest(string? value) => value is not null && value.Length == 64 &&
        value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    internal static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    internal static byte[] DigestBytes(string hex)
    {
        if (!IsDigest(hex)) throw new InvalidDataException("Noncanonical checkpoint digest");
        var result = new byte[32];
        for (var i = 0; i < result.Length; i++) result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return result;
    }
}

// Prepare once on/after a trusted consistent capture. Reuse this immutable object
// across reconnects; no Verse access occurs in the socket worker.
public sealed class PreparedWorldViewTransfer
{
    private readonly byte[] payload;
    public WorldViewOffer Offer { get; }
    public int ChunkCount { get; }

    public PreparedWorldViewTransfer(WorldViewCheckpoint checkpoint,
        int chunkBytes = WorldViewTransferFormat.DefaultChunkBytes)
    {
        if (chunkBytes < WorldViewTransferFormat.MinChunkBytes || chunkBytes > WorldViewTransferFormat.MaxChunkBytes)
            throw new ArgumentOutOfRangeException(nameof(chunkBytes));
        var clean = WorldViewCheckpointValidator.ValidateAndCopy(checkpoint);
        var bootstrap = WorldBootstrapCodec.Encode(clean.Bootstrap);
        var graph = EntityGraphCodec.Encode(clean.Graph);
        payload = new byte[checked(bootstrap.Length + graph.Length)];
        Buffer.BlockCopy(bootstrap, 0, payload, 0, bootstrap.Length);
        Buffer.BlockCopy(graph, 0, payload, bootstrap.Length, graph.Length);
        Offer = new(WorldViewTransferFormat.Version, WorldBootstrapFormat.Version, EntityGraphFormat.Version,
            Guid.NewGuid(), clean.Bootstrap.WorldId, clean.Bootstrap.NetworkVersion, clean.Bootstrap.GameStateVersion,
            bootstrap.Length, graph.Length, chunkBytes, WorldViewTransferValidation.ContentDigest(clean.Bootstrap.Manifest),
            WorldViewTransferValidation.Digest(payload));
        ChunkCount = WorldViewTransferValidation.ValidateOffer(Offer);
    }

    public WorldViewChunk GetChunk(int index)
    {
        if (index < 0 || index >= ChunkCount) throw new ArgumentOutOfRangeException(nameof(index));
        var offset = checked(index * Offer.ChunkBytes);
        var copy = new byte[Math.Min(Offer.ChunkBytes, payload.Length - offset)];
        Buffer.BlockCopy(payload, offset, copy, 0, copy.Length);
        return new(Offer.TransferId, index, copy, WorldViewTransferValidation.Digest(copy));
    }
}
