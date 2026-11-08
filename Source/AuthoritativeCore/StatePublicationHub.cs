namespace Multiplayer.Authoritative;

// One simulation-thread producer, multiple network readers.
// Uses a bounded delta history. On a gap the caller MUST send a fresh snapshot.
// Nothing in this class executes gameplay or allocates RimWorld objects.
public sealed class StatePublicationHub
{
    private readonly object gate = new();
    private readonly WorldStateReplicator producer = new();
    private readonly Queue<WorldDelta> history = new();
    private readonly int historyLimit;
    private WorldSnapshot? current;

    public StatePublicationHub(int historyLimit = 256)
    {
        if (historyLimit <= 0) throw new ArgumentOutOfRangeException(nameof(historyLimit));
        this.historyLimit = historyLimit;
    }

    public void Initialize(WorldSnapshot gameState)
    {
        lock (gate)
        {
            if (current != null)
                throw new InvalidOperationException("State feed already initialized");
            current = producer.Start(gameState);
        }
    }

    public WorldDelta? Publish(WorldSnapshot gameState)
    {
        lock (gate)
        {
            if (current is null)
                throw new InvalidOperationException("Initialize the state feed first");
            var change = producer.CaptureChanges(gameState);
            current = producer.CurrentSnapshot();
            if (change != null)
            {
                history.Enqueue(change);
                while (history.Count > historyLimit)
                    history.Dequeue();
            }
            return change;
        }
    }

    public WorldSnapshot LatestSnapshot()
    {
        lock (gate)
        {
            if (current is null)
                throw new InvalidOperationException("State feed is not initialized");
            return new WorldSnapshot(current.Version, current.Things.ToArray())
            {
                GameStateVersion = current.GameStateVersion
            };
        }
    }

    // Returns false on a missing history window, unknown future version or
    // a gap. The caller must send LatestSnapshot() instead.
    public bool TryReadAfter(long version, out IReadOnlyList<WorldDelta> changes)
    {
        lock (gate)
        {
            changes = Array.Empty<WorldDelta>();
            if (current is null || version < 0 || version > current.Version)
                return false;
            if (version == current.Version)
                return true;
            if (history.Count == 0 || history.Peek().BaseVersion > version)
                return false;

            var result = new List<WorldDelta>();
            long previous = version;
            foreach (var item in history)
            {
                if (item.Version <= version)
                    continue;
                if (item.BaseVersion != previous)
                    return false;
                result.Add(new WorldDelta(item.BaseVersion, item.Version, item.Changes.ToArray())
                {
                    GameStateVersion = item.GameStateVersion
                });
                previous = item.Version;
            }
            if (previous != current.Version)
                return false;
            changes = result;
            return true;
        }
    }
}
