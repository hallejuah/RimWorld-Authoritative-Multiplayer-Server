namespace Multiplayer.Authoritative;

// Proof-of-concept snapshot-to-delta publisher. A caller chooses network update
// frequency independently of game ticks. This scans simplified entity DTOs and
// must be replaced with dirty tracking for real RimWorld world-scale replication.
public sealed class WorldStateReplicator
{
    private Dictionary<long, ThingState> published = new();
    private long previousGameVersion;
    public long Version { get; private set; }
    public bool Initialized { get; private set; }

    public WorldSnapshot Start(WorldSnapshot gameState)
    {
        var next = Validate(gameState);
        published = next;
        previousGameVersion = gameState.Version;
        Version = 0;
        Initialized = true;
        return CurrentSnapshot();
    }

    public WorldDelta? CaptureChanges(WorldSnapshot gameState)
    {
        if (!Initialized)
            throw new InvalidOperationException("Call Start before capturing deltas");
        var next = Validate(gameState);
        if (gameState.Version < previousGameVersion)
            throw new InvalidOperationException("Game state version moved backwards");

        var changes = new List<StateChange>();
        foreach (var id in published.Keys.OrderBy(id => id))
        {
            if (!next.ContainsKey(id))
                changes.Add(new StateChange(ChangeKind.Delete, id, null));
        }
        foreach (var thing in Sorted(next))
        {
            if (!published.TryGetValue(thing.Id, out var before))
                changes.Add(new StateChange(ChangeKind.Create, thing.Id, thing));
            else
            {
                if (before.DomainId != thing.DomainId || before.FactionId != thing.FactionId)
                    throw new InvalidOperationException("Entity ownership migration requires an explicit transaction");
                if (before != thing)
                    changes.Add(new StateChange(ChangeKind.Update, thing.Id, thing));
            }
        }

        var newVersion = changes.Count == 0 ? Version : checked(Version + 1);
        // Only commit the producer's baseline after all changes are validated.
        published = next;
        previousGameVersion = gameState.Version;
        if (changes.Count == 0)
            return null;

        var delta = new WorldDelta(Version, newVersion, changes.ToArray())
        {
            GameStateVersion = gameState.Version
        };
        Version = newVersion;
        return delta;
    }

    public WorldSnapshot CurrentSnapshot()
    {
        if (!Initialized)
            throw new InvalidOperationException("Publisher has not been initialized");
        return new WorldSnapshot(Version, Sorted(published).ToArray())
        {
            GameStateVersion = previousGameVersion
        };
    }

    private static Dictionary<long, ThingState> Validate(WorldSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.Version < 0 || snapshot.Things is null)
            throw new ArgumentException("Invalid game-state snapshot", nameof(snapshot));

        var next = new Dictionary<long, ThingState>();
        foreach (var thing in snapshot.Things)
        {
            if (thing is null || thing.Id <= 0 || thing.DomainId <= 0 ||
                thing.FactionId <= 0 || next.ContainsKey(thing.Id))
                throw new ArgumentException("Duplicate or invalid entity in snapshot", nameof(snapshot));
            next.Add(thing.Id, thing);
        }
        return next;
    }

    private static IEnumerable<ThingState> Sorted(Dictionary<long, ThingState> values) =>
        values.Values.OrderBy(thing => thing.Id);
}
