namespace Multiplayer.Authoritative;

public sealed record DomainClockState(int DomainId, int FactionId, long Tick, int Speed, double PendingTicks);

// Serial scheduler: all callbacks execute on the creating thread.
// No assumption is made that Unity/RimWorld is thread-safe.
public sealed class SimulationDomainManager
{
    private const double TicksPerSecond = 60d;
    private const int MaxTicksPerDomainPerAdvance = 240;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly SortedDictionary<int, DomainClockState> domains = new();

    public void AddDomain(int domainId, int factionId, long startingTick = 0)
    {
        EnsureThread();
        if (domainId <= 0 || factionId <= 0 || startingTick < 0)
            throw new ArgumentOutOfRangeException(nameof(domainId));
        if (domains.ContainsKey(domainId))
            throw new InvalidOperationException("Domain ID already exists");
        domains.Add(domainId, new DomainClockState(domainId, factionId, startingTick, 0, 0));
    }

    public void SetSpeed(int domainId, int speed)
    {
        EnsureThread();
        if (speed is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(speed));
        var state = domains[domainId];
        domains[domainId] = state with { Speed = speed };
    }

    public DomainClockState GetDomain(int domainId) => domains[domainId];

    public IReadOnlyList<DomainClockState> ExportClocks()
    {
        EnsureThread();
        return domains.Values.ToArray();
    }

    public void RestoreClocks(IReadOnlyList<DomainClockState> saved)
    {
        EnsureThread();
        if (saved is null) throw new ArgumentNullException(nameof(saved));
        var replacement = new SortedDictionary<int, DomainClockState>();
        foreach (var state in saved)
        {
            if (state is null || state.DomainId <= 0 || state.FactionId <= 0 ||
                state.Tick < 0 || state.Speed is < 0 or > 3 ||
                double.IsNaN(state.PendingTicks) || double.IsInfinity(state.PendingTicks) ||
                state.PendingTicks < 0 || replacement.ContainsKey(state.DomainId))
                throw new ArgumentException("Invalid saved domain clocks", nameof(saved));
            replacement.Add(state.DomainId, state);
        }
        domains.Clear();
        foreach (var pair in replacement)
            domains.Add(pair.Key, pair.Value);
    }

    public void Advance(TimeSpan elapsed, Action<int, long> executeGameTick)
    {
        EnsureThread();
        if (executeGameTick is null) throw new ArgumentNullException(nameof(executeGameTick));
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(elapsed));

        foreach (var id in domains.Keys.ToArray())
        {
            var state = domains[id];
            if (state.Speed == 0)
                continue;
            var pending = state.PendingTicks + elapsed.TotalSeconds * TicksPerSecond * state.Speed;
            var toRun = (int)Math.Min(Math.Floor(pending), MaxTicksPerDomainPerAdvance);
            // Retain accumulated work even if a game callback throws. Only ticks
            // actually completed are removed, preventing lost or replayed time.
            state = state with { PendingTicks = pending };
            domains[id] = state;
            for (var i = 0; i < toRun; i++)
            {
                var nextTick = checked(state.Tick + 1);
                executeGameTick(id, nextTick);
                state = state with { Tick = nextTick, PendingTicks = state.PendingTicks - 1 };
                domains[id] = state;
            }
        }
    }

    private void EnsureThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Domain simulation must run on its owner thread");
    }
}
