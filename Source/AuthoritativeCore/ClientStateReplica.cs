namespace Multiplayer.Authoritative;

public enum ReplicaApplyResult
{
    Applied,
    NeedsSnapshot
}

// This is a read-only network model. It must not call RimWorld simulation methods.
public sealed class ClientStateReplica
{
    private Dictionary<long, ThingState> things = new();
    public bool Initialized { get; private set; }
    public long Version { get; private set; }
    public IReadOnlyDictionary<long, ThingState> Things => things;

    public ReplicaApplyResult InstallSnapshot(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Version < 0 || snapshot.Things is null)
            return ReplicaApplyResult.NeedsSnapshot;

        var candidate = new Dictionary<long, ThingState>();
        foreach (var thing in snapshot.Things)
        {
            if (!Valid(thing) || !candidate.TryAdd(thing.Id, thing))
                return ReplicaApplyResult.NeedsSnapshot;
        }

        // Swap only after validating the complete snapshot.
        things = candidate;
        Version = snapshot.Version;
        Initialized = true;
        return ReplicaApplyResult.Applied;
    }

    public ReplicaApplyResult ApplyDelta(WorldDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (!Initialized || delta.Changes is null ||
            delta.BaseVersion != Version || delta.Version != Version + 1)
            return ReplicaApplyResult.NeedsSnapshot;

        var candidate = new Dictionary<long, ThingState>(things);
        foreach (var change in delta.Changes)
        {
            if (change is null || change.ThingId <= 0)
                return ReplicaApplyResult.NeedsSnapshot;

            switch (change.Kind)
            {
                case ChangeKind.Create:
                    if (change.State is null || change.State.Id != change.ThingId ||
                        !Valid(change.State) || !candidate.TryAdd(change.ThingId, change.State))
                        return ReplicaApplyResult.NeedsSnapshot;
                    break;
                case ChangeKind.Update:
                    if (change.State is null || change.State.Id != change.ThingId ||
                        !Valid(change.State) ||
                        !candidate.TryGetValue(change.ThingId, out var old) ||
                        old.DomainId != change.State.DomainId ||
                        old.FactionId != change.State.FactionId)
                        return ReplicaApplyResult.NeedsSnapshot;
                    candidate[change.ThingId] = change.State;
                    break;
                case ChangeKind.Delete:
                    if (change.State is not null || !candidate.Remove(change.ThingId))
                        return ReplicaApplyResult.NeedsSnapshot;
                    break;
                default:
                    return ReplicaApplyResult.NeedsSnapshot;
            }
        }

        things = candidate;
        Version = delta.Version;
        return ReplicaApplyResult.Applied;
    }

    private static bool Valid(ThingState? thing) =>
        thing is { Id: > 0, DomainId: > 0, FactionId: > 0 };
}
