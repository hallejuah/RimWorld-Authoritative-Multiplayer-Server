namespace Multiplayer.Authoritative;

// An in-memory atomic presentation unit, NOT a new transport message or save.
public sealed record WorldViewCheckpoint(WorldBootstrapV2 Bootstrap, EntityGraphSnapshot Graph);

public static class WorldViewCheckpointValidator
{
    public static WorldViewCheckpoint ValidateAndCopy(WorldViewCheckpoint source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        var bootstrap = WorldBootstrapValidator.ValidateAndCopy(source.Bootstrap);
        var graph = EntityGraphValidator.ValidateAndCopy(source.Graph);
        if (bootstrap.WorldId != graph.WorldId || bootstrap.NetworkVersion != graph.NetworkVersion ||
            bootstrap.GameStateVersion != graph.GameStateVersion || bootstrap.Maps.Count != graph.Maps.Count)
            throw new ArgumentException("Map and entity graph checkpoint identities differ", nameof(source));
        for (var i = 0; i < bootstrap.Maps.Count; i++)
        {
            var map = bootstrap.Maps[i];
            var scope = graph.Maps[i];
            if (map.DomainId != scope.DomainId || map.Width != scope.Width || map.Height != scope.Height)
                throw new ArgumentException("Entity graph map scope differs from bootstrap", nameof(source));
        }
        var roots = graph.Nodes.Where(n => n.Attachment.HolderEntityId == 0).Select(n => n.Entity).ToArray();
        if (roots.Length != bootstrap.Entities.Count)
            throw new ArgumentException("Bootstrap and graph have different spawned entities", nameof(source));
        for (var i = 0; i < roots.Length; i++)
            if (roots[i] != bootstrap.Entities[i])
                throw new ArgumentException("Spawned entity state differs between checkpoint parts", nameof(source));
        // An accepted pair must fit each format's byte budget, not just its item counts.
        WorldBootstrapCodec.Encode(bootstrap);
        EntityGraphCodec.Encode(graph);
        return new(bootstrap, graph);
    }
}

public enum WorldViewInstallResult
{
    Accepted = 0,
    InvalidState = 1,
    WrongWorld = 2,
    IncompatibleContent = 3,
    StaleRevision = 4,
    RevisionConflict = 5
}

// Single presentation-thread replica; it never constructs Verse Things or ticks
// gameplay. Authorization/interest filtering must precede delivery to this API.
public sealed class ClientWorldViewReplica
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly ContentManifest localManifest;
    private readonly Guid requiredWorldId;
    private WorldViewCheckpoint? installed;
    private byte[]? installedBootstrapBytes;
    private byte[]? installedGraphBytes;

    public ClientWorldViewReplica(ContentManifest localManifest, Guid requiredWorldId)
    {
        this.localManifest = WorldBootstrapValidator.ValidateManifest(localManifest);
        if (requiredWorldId == Guid.Empty) throw new ArgumentException("Expected server world is required", nameof(requiredWorldId));
        this.requiredWorldId = requiredWorldId;
    }

    public long NetworkVersion { get { EnsureThread(); return installed?.Bootstrap.NetworkVersion ?? -1; } }

    public WorldViewCheckpoint GetSnapshotCopy()
    {
        EnsureThread();
        return installed is null ? throw new InvalidOperationException("No world view installed") :
            WorldViewCheckpointValidator.ValidateAndCopy(installed);
    }

    public WorldViewInstallResult TryInstall(WorldViewCheckpoint candidate)
    {
        EnsureThread();
        WorldViewCheckpoint clean;
        try { clean = WorldViewCheckpointValidator.ValidateAndCopy(candidate); }
        catch (ArgumentException) { return WorldViewInstallResult.InvalidState; }
        if (clean.Bootstrap.WorldId != requiredWorldId) return WorldViewInstallResult.WrongWorld;
        if (!ClientWorldBootstrapReplica.Compatible(localManifest, clean.Bootstrap.Manifest))
            return WorldViewInstallResult.IncompatibleContent;
        if (installed is not null && (clean.Bootstrap.NetworkVersion < installed.Bootstrap.NetworkVersion ||
            clean.Bootstrap.GameStateVersion < installed.Bootstrap.GameStateVersion))
            return WorldViewInstallResult.StaleRevision;
        var bootstrapBytes = WorldBootstrapCodec.Encode(clean.Bootstrap);
        var graphBytes = EntityGraphCodec.Encode(clean.Graph);
        if (installed is not null && clean.Bootstrap.NetworkVersion == installed.Bootstrap.NetworkVersion)
        {
            return bootstrapBytes.SequenceEqual(installedBootstrapBytes!) && graphBytes.SequenceEqual(installedGraphBytes!)
                ? WorldViewInstallResult.Accepted : WorldViewInstallResult.RevisionConflict;
        }
        // All validation and serialization finished before this commit point.
        installedBootstrapBytes = bootstrapBytes;
        installedGraphBytes = graphBytes;
        installed = clean;
        return WorldViewInstallResult.Accepted;
    }

    private void EnsureThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("World view belongs to its presentation thread");
    }
}
