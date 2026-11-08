using System.Text.Json;
using Multiplayer.Authoritative;

namespace Multiplayer.Authoritative.Transport;

public enum WorldViewOfferResult { Started, Resumed, InvalidOffer, WrongWorld, IncompatibleContent, StaleRevision, Conflict }
public enum WorldViewChunkResult { Accepted, Duplicate, NoTransfer, ForeignTransfer, InvalidChunk, Conflict }

// One bounded staging buffer per instance. Methods are serialized by a lock so
// socket continuations may change threads. The actual client replica still MUST
// be installed on its presentation thread. Never mutates an installed world view.
public sealed class WorldViewTransferAssembler
{
    private readonly object gate = new();
    private readonly long minimumNetworkVersion;
    private readonly long minimumGameStateVersion;
    private WorldViewOffer? offer;
    private byte[]? payload;
    private bool[]? received;
    private int receivedCount;
    private int nextMissing;

    public Guid WorldId { get; }
    public string ContentSha256 { get; }
    public WorldViewOffer? Offer { get { lock (gate) return offer; } }
    public int ReceivedCount { get { lock (gate) return receivedCount; } }
    public int BufferedBytes { get { lock (gate) return payload?.Length ?? 0; } }

    public WorldViewTransferAssembler(ContentManifest localManifest, Guid requiredWorldId,
        long minimumNetworkVersion = 0, long minimumGameStateVersion = 0)
    {
        if (requiredWorldId == Guid.Empty || minimumNetworkVersion < 0 || minimumGameStateVersion < 0)
            throw new ArgumentException("Invalid expected world/revision floor");
        WorldId = requiredWorldId;
        ContentSha256 = WorldViewTransferValidation.ContentDigest(localManifest);
        this.minimumNetworkVersion = minimumNetworkVersion;
        this.minimumGameStateVersion = minimumGameStateVersion;
    }

    public WorldViewOfferResult Begin(WorldViewOffer candidate)
    {
        int chunks;
        try { chunks = WorldViewTransferValidation.ValidateOffer(candidate); }
        catch (ArgumentException) { return WorldViewOfferResult.InvalidOffer; }
        lock (gate)
        {
            if (candidate.WorldId != WorldId) return WorldViewOfferResult.WrongWorld;
            if (candidate.ContentSha256 != ContentSha256) return WorldViewOfferResult.IncompatibleContent;
            if (candidate.NetworkVersion < minimumNetworkVersion || candidate.GameStateVersion < minimumGameStateVersion)
                return WorldViewOfferResult.StaleRevision;
            if (offer is not null)
            {
                if (candidate.TransferId == offer.TransferId && candidate != offer)
                    return WorldViewOfferResult.Conflict;
                if (candidate.NetworkVersion < offer.NetworkVersion || candidate.GameStateVersion < offer.GameStateVersion)
                    return WorldViewOfferResult.StaleRevision;
                // A new connection/transfer may re-chunk the SAME checkpoint, but
                // cannot silently rewrite the contents of one network revision.
                if (candidate.NetworkVersion == offer.NetworkVersion &&
                    (candidate.GameStateVersion != offer.GameStateVersion || candidate.PayloadSha256 != offer.PayloadSha256 ||
                     candidate.BootstrapBytes != offer.BootstrapBytes || candidate.GraphBytes != offer.GraphBytes))
                    return WorldViewOfferResult.Conflict;
                if (candidate == offer && payload is not null) return WorldViewOfferResult.Resumed;
            }
            // Allocate before replacing the old state. There is never a partial
            // switch to a new offer if validation/allocation fails.
            var buffer = new byte[checked(candidate.BootstrapBytes + candidate.GraphBytes)];
            var flags = new bool[chunks];
            offer = candidate;
            payload = buffer;
            received = flags;
            receivedCount = 0;
            nextMissing = 0;
            return WorldViewOfferResult.Started;
        }
    }

    public WorldViewChunkResult Accept(WorldViewChunk chunk)
    {
        lock (gate)
        {
            if (offer is null || payload is null || received is null) return WorldViewChunkResult.NoTransfer;
            if (chunk is null) return WorldViewChunkResult.InvalidChunk;
            if (chunk.TransferId != offer.TransferId) return WorldViewChunkResult.ForeignTransfer;
            if (chunk.Index < 0 || chunk.Index >= received.Length || chunk.Payload is null ||
                !WorldViewTransferValidation.IsDigest(chunk.Sha256)) return WorldViewChunkResult.InvalidChunk;
            var offset = checked(chunk.Index * offer.ChunkBytes);
            var expected = Math.Min(offer.ChunkBytes, payload.Length - offset);
            if (chunk.Payload.Length != expected) return WorldViewChunkResult.InvalidChunk;
            // Verify and store the same defensive copy, not a caller-owned array.
            var bytes = (byte[])chunk.Payload.Clone();
            if (WorldViewTransferValidation.Digest(bytes) != chunk.Sha256) return WorldViewChunkResult.InvalidChunk;
            if (received[chunk.Index])
            {
                for (var i = 0; i < bytes.Length; i++)
                    if (payload[offset + i] != bytes[i]) return WorldViewChunkResult.Conflict;
                return WorldViewChunkResult.Duplicate;
            }
            Buffer.BlockCopy(bytes, 0, payload, offset, bytes.Length);
            received[chunk.Index] = true;
            receivedCount++;
            while (nextMissing < received.Length && received[nextMissing]) nextMissing++;
            return WorldViewChunkResult.Accepted;
        }
    }

    // Pull window is bounded by the caller; the TCP implementation uses one.
    public int[] GetMissingIndices(int maximum = 64)
    {
        if (maximum <= 0 || maximum > 64) throw new ArgumentOutOfRangeException(nameof(maximum));
        lock (gate)
        {
            if (received is null) throw new InvalidOperationException("No transfer buffer");
            var result = new List<int>(maximum);
            for (var i = nextMissing; i < received.Length && result.Count < maximum; i++)
                if (!received[i]) result.Add(i);
            return result.ToArray();
        }
    }

    public WorldViewCheckpoint GetValidatedCheckpoint()
    {
        lock (gate)
        {
            if (offer is null || payload is null || received is null || receivedCount != received.Length)
                throw new InvalidOperationException("Checkpoint transfer is incomplete");
            try
            {
                if (WorldViewTransferValidation.Digest(payload) != offer.PayloadSha256)
                    throw new InvalidDataException("Whole checkpoint checksum mismatch");
                var bootstrapBytes = new byte[offer.BootstrapBytes];
                var graphBytes = new byte[offer.GraphBytes];
                Buffer.BlockCopy(payload, 0, bootstrapBytes, 0, bootstrapBytes.Length);
                Buffer.BlockCopy(payload, bootstrapBytes.Length, graphBytes, 0, graphBytes.Length);
                var bootstrap = WorldBootstrapCodec.Decode(bootstrapBytes);
                var graph = EntityGraphCodec.Decode(graphBytes, WorldId);
                if (bootstrap.WorldId != offer.WorldId || bootstrap.NetworkVersion != offer.NetworkVersion ||
                    bootstrap.GameStateVersion != offer.GameStateVersion ||
                    WorldViewTransferValidation.ContentDigest(bootstrap.Manifest) != ContentSha256)
                    throw new InvalidDataException("Decoded checkpoint differs from its offer");
                return WorldViewCheckpointValidator.ValidateAndCopy(new(bootstrap, graph));
            }
            catch (Exception e) when (e is ArgumentException || e is JsonException || e is InvalidDataException)
            {
                ReleaseBuffer();
                throw new InvalidDataException("Rejected complete checkpoint; request a fresh transfer", e);
            }
        }
    }

    // Explicit abort/expiry cleanup. The last offer's revision/content guard is
    // retained; reopening the identical offer allocates a fresh buffer.
    public void DiscardBuffers() { lock (gate) ReleaseBuffer(); }

    private void ReleaseBuffer()
    {
        payload = null;
        received = null;
        receivedCount = 0;
        nextMissing = 0;
    }
}
