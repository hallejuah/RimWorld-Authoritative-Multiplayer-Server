namespace Multiplayer.Authoritative;

public enum BootstrapAdmissionResult
{
    Accepted = 0,
    InvalidState = 1,
    WrongWorld = 2,
    IncompatibleContent = 3,
    StaleRevision = 4
}

// Client's installed bootstrap never invokes Verse, pathfinding or TickManager.
// The original v1 ClientStateReplica remains independent for local probe traffic.
public sealed class ClientWorldBootstrapReplica
{
    private readonly ContentManifest localManifest;
    private readonly Guid? requiredWorld;
    private WorldBootstrapV2? installed;

    public ClientWorldBootstrapReplica(ContentManifest locallyInstalledMods,
        Guid? requiredWorldId = null)
    {
        localManifest = WorldBootstrapValidator.ValidateManifest(locallyInstalledMods);
        if (requiredWorldId == Guid.Empty)
            throw new ArgumentException("A required world ID cannot be empty", nameof(requiredWorldId));
        requiredWorld = requiredWorldId;
    }

    public bool Initialized => installed is not null;
    public Guid? WorldId => installed?.WorldId;
    public long NetworkVersion => installed?.NetworkVersion ?? -1;

    // Expose a deep copy: users cannot mutate private accepted map buffers.
    public WorldBootstrapV2 GetSnapshotCopy() =>
        installed is null
            ? throw new InvalidOperationException("No bootstrap installed")
            : WorldBootstrapValidator.ValidateAndCopy(installed);

    public BootstrapAdmissionResult TryInstall(WorldBootstrapV2 candidate)
    {
        WorldBootstrapV2 validated;
        try
        {
            validated = WorldBootstrapValidator.ValidateAndCopy(candidate);
        }
        catch (ArgumentException)
        {
            return BootstrapAdmissionResult.InvalidState;
        }

        if ((requiredWorld.HasValue && requiredWorld.Value != validated.WorldId) ||
            (installed is not null && installed.WorldId != validated.WorldId))
            return BootstrapAdmissionResult.WrongWorld;
        if (!Compatible(localManifest, validated.Manifest))
            return BootstrapAdmissionResult.IncompatibleContent;
        if (installed is not null &&
            (validated.NetworkVersion < installed.NetworkVersion ||
             validated.GameStateVersion < installed.GameStateVersion))
            return BootstrapAdmissionResult.StaleRevision;

        installed = validated;
        return BootstrapAdmissionResult.Accepted;
    }

    public static bool Compatible(ContentManifest left, ContentManifest right)
    {
        var local = WorldBootstrapValidator.ValidateManifest(left);
        var server = WorldBootstrapValidator.ValidateManifest(right);
        if (!string.Equals(local.RimWorldVersion, server.RimWorldVersion, StringComparison.Ordinal) ||
            !string.Equals(local.DefCatalogSha256, server.DefCatalogSha256, StringComparison.Ordinal) ||
            local.Mods.Count != server.Mods.Count)
            return false;
        for (var i = 0; i < local.Mods.Count; i++)
            if (local.Mods[i] != server.Mods[i])
                return false;
        return true;
    }
}
