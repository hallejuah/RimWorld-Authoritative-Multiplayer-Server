using System.Runtime.CompilerServices;

namespace Multiplayer.Authoritative;

// Runtime references are never serialized. Rebuilt only from trusted server
// objects after loading the registry. The maps deliberately use reference equality.
public sealed class ServerEntityBindings<T> where T : class
{
    private readonly ServerEntityRegistry registry;
    private readonly Dictionary<string, T> bySource = new(StringComparer.Ordinal);
    private readonly Dictionary<T, string> byObject = new(new ObjectIdentityComparer());

    public ServerEntityBindings(ServerEntityRegistry registry) =>
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public long GetOrAssignId(string sourceKey, T entity)
    {
        ValidateBinding(sourceKey, entity);
        var id = registry.GetOrCreateId(sourceKey);
        Remember(sourceKey, entity);
        return id;
    }

    public bool TryGetId(string sourceKey, T entity, out long entityId)
    {
        ValidateBinding(sourceKey, entity);
        if (!registry.TryGetId(sourceKey, out entityId)) return false;
        Remember(sourceKey, entity);
        return true;
    }

    public bool TryResolve(long entityId, out T? entity)
    {
        registry.AssertAccess();
        if (registry.TryGetSourceKey(entityId, out var sourceKey) &&
            sourceKey is not null && bySource.TryGetValue(sourceKey, out var found))
        {
            entity = found;
            return true;
        }
        entity = null;
        return false;
    }

    public bool Retire(T entity)
    {
        registry.AssertAccess();
        if (entity is null) throw new ArgumentNullException(nameof(entity));
        return byObject.TryGetValue(entity, out var sourceKey) &&
            registry.TryGetId(sourceKey, out var id) && registry.Retire(id);
    }

    // Used at save time, not on every simulation tick. Retired references stay
    // reserved in this bounded binding epoch so a stale object cannot be rebound.
    public IReadOnlyList<T> CaptureBoundObjects()
    {
        registry.AssertAccess();
        return Array.AsReadOnly(byObject.Keys.ToArray());
    }

    private void ValidateBinding(string sourceKey, T entity)
    {
        registry.AssertAccess();
        if (entity is null) throw new ArgumentNullException(nameof(entity));
        ServerEntityRegistry.ValidateSourceKey(sourceKey);
        if (bySource.TryGetValue(sourceKey, out var previous) && !ReferenceEquals(previous, entity))
            throw new InvalidOperationException("Two server objects share one save-reference");
        if (byObject.TryGetValue(entity, out var previousKey) &&
            !string.Equals(previousKey, sourceKey, StringComparison.Ordinal))
            throw new InvalidOperationException("A server object changed its save-reference");
    }

    private void Remember(string sourceKey, T entity)
    {
        bySource[sourceKey] = entity;
        byObject[entity] = sourceKey;
    }

    private sealed class ObjectIdentityComparer : IEqualityComparer<T>
    {
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
