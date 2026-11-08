using System.Text;

namespace Multiplayer.Authoritative;

public static class EntityIdentityFormat
{
    public const int Version = 1;
    public const int MaxEntities = 100_000;
    public const int MaxSourceKeyBytes = 256;
    public const int MaxBytes = 32 * 1024 * 1024;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
}

// SourceKey is a trusted server save-reference key, NEVER a client-supplied ID.
// Retired entries remain reserved for the lifetime of this world.
public sealed record EntityIdentityEntry(long EntityId, string SourceKey, bool Retired);
public sealed record EntityIdentityCheckpoint(
    int FormatVersion, Guid WorldId, long NextEntityId,
    IReadOnlyList<EntityIdentityEntry> Entries);

// Single simulation-thread registry. Network identity is (WorldId, EntityId).
// It does not authorize factions, transfer ownership or serialize Thing graphs.
public sealed class ServerEntityRegistry
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, EntityIdentityEntry> bySource = new(StringComparer.Ordinal);
    private readonly Dictionary<long, EntityIdentityEntry> byId = new();
    private readonly int maximumEntities;
    private long nextEntityId = 1;

    public Guid WorldId { get; }
    public int Count { get { AssertAccess(); return byId.Count; } }
    public long NextEntityId { get { AssertAccess(); return nextEntityId; } }

    public ServerEntityRegistry(Guid worldId, int maximumEntities = EntityIdentityFormat.MaxEntities)
    {
        if (worldId == Guid.Empty) throw new ArgumentException("World identity is required", nameof(worldId));
        if (maximumEntities <= 0 || maximumEntities > EntityIdentityFormat.MaxEntities)
            throw new ArgumentOutOfRangeException(nameof(maximumEntities));
        WorldId = worldId;
        this.maximumEntities = maximumEntities;
    }

    public long GetOrCreateId(string sourceKey)
    {
        AssertAccess();
        ValidateSourceKey(sourceKey);
        if (bySource.TryGetValue(sourceKey, out var existing))
        {
            if (existing.Retired)
                throw new InvalidOperationException("A retired server save-reference cannot be reused");
            return existing.EntityId;
        }
        // long.MaxValue is the exhausted allocator sentinel, never an issued ID.
        if (byId.Count >= maximumEntities || nextEntityId == long.MaxValue)
            throw new InvalidOperationException("Server entity identity budget exhausted");
        var entry = new EntityIdentityEntry(nextEntityId, sourceKey, false);
        bySource.Add(sourceKey, entry);
        byId.Add(entry.EntityId, entry);
        nextEntityId++;
        return entry.EntityId;
    }

    // Lookup must not allocate an ID, particularly while resolving a command.
    public bool TryGetId(string sourceKey, out long entityId)
    {
        AssertAccess();
        ValidateSourceKey(sourceKey);
        if (bySource.TryGetValue(sourceKey, out var entry) && !entry.Retired)
        {
            entityId = entry.EntityId;
            return true;
        }
        entityId = 0;
        return false;
    }

    public bool TryGetSourceKey(long entityId, out string? sourceKey)
    {
        AssertAccess();
        if (byId.TryGetValue(entityId, out var entry) && !entry.Retired)
        {
            sourceKey = entry.SourceKey;
            return true;
        }
        sourceKey = null;
        return false;
    }

    public bool Retire(long entityId)
    {
        AssertAccess();
        if (!byId.TryGetValue(entityId, out var entry) || entry.Retired) return false;
        var retired = entry with { Retired = true };
        byId[entityId] = retired;
        bySource[entry.SourceKey] = retired;
        return true;
    }

    public EntityIdentityCheckpoint CaptureCheckpoint()
    {
        AssertAccess();
        return new EntityIdentityCheckpoint(EntityIdentityFormat.Version, WorldId, nextEntityId,
            Array.AsReadOnly(byId.Values.OrderBy(e => e.EntityId).ToArray()));
    }

    // Build a new registry; never partially mutate a running registry on failure.
    public static ServerEntityRegistry Restore(EntityIdentityCheckpoint checkpoint,
        Guid expectedWorldId, int maximumEntities = EntityIdentityFormat.MaxEntities)
    {
        if (checkpoint is null) throw new ArgumentNullException(nameof(checkpoint));
        if (expectedWorldId == Guid.Empty || checkpoint.WorldId != expectedWorldId ||
            checkpoint.FormatVersion != EntityIdentityFormat.Version || checkpoint.NextEntityId <= 0 ||
            checkpoint.Entries is null || checkpoint.Entries.Count > maximumEntities)
            throw new InvalidDataException("Invalid or foreign entity identity checkpoint");
        var candidate = new ServerEntityRegistry(expectedWorldId, maximumEntities);
        foreach (var entry in checkpoint.Entries)
        {
            if (entry is null || entry.EntityId <= 0 || entry.EntityId >= checkpoint.NextEntityId)
                throw new InvalidDataException("Invalid entity identity or allocator high-water mark");
            try { ValidateSourceKey(entry.SourceKey); }
            catch (ArgumentException e) { throw new InvalidDataException("Invalid server save-reference", e); }
            if (candidate.byId.ContainsKey(entry.EntityId) || candidate.bySource.ContainsKey(entry.SourceKey))
                throw new InvalidDataException("Duplicate entity identity or server save-reference");
            candidate.byId.Add(entry.EntityId, entry);
            candidate.bySource.Add(entry.SourceKey, entry);
        }
        candidate.nextEntityId = checkpoint.NextEntityId;
        return candidate;
    }

    internal void AssertAccess()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Entity identities belong to the simulation thread");
    }

    internal static void ValidateSourceKey(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || sourceKey.Length > EntityIdentityFormat.MaxSourceKeyBytes ||
            !string.Equals(sourceKey, sourceKey.Trim(), StringComparison.Ordinal) || sourceKey.Any(char.IsControl))
            throw new ArgumentException("Invalid server save-reference", nameof(sourceKey));
        try
        {
            if (EntityIdentityFormat.Utf8.GetByteCount(sourceKey) > EntityIdentityFormat.MaxSourceKeyBytes)
                throw new ArgumentException("Server save-reference exceeds its UTF-8 budget", nameof(sourceKey));
        }
        catch (EncoderFallbackException e)
        {
            throw new ArgumentException("Invalid UTF-16 server save-reference", nameof(sourceKey), e);
        }
    }
}
