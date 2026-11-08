using System;
using Multiplayer.Authoritative;
using Verse;

namespace Multiplayer.Authoritative.RimWorldBridge;

// Must be invoked from a loaded game on Unity's main thread, never a socket
// thread. The caller supplies an actual authenticated-session policy.
public sealed class LoadedSingleMapHost
{
    public RimWorldSingleMapDriver Driver { get; }
    public AuthoritativeSimulationHost Host { get; }
    public WorldStateReplicator Replication { get; } = new();
    public StatePublicationHub StateFeed { get; } = new();

    // Called once, on Unity thread, when the opt-in loop is activated.
    // The network worker only reads the thread-safe StateFeed afterwards.
    internal void BeginStateFeed() => StateFeed.Initialize(Host.CaptureSnapshot());
    internal void PublishState() => StateFeed.Publish(Host.CaptureSnapshot());

    // Network initial state and subsequent deltas must be captured on the
    // Unity simulation thread; consumers can encode them via ProtocolCodec.
    // The projection currently contains only pawn positions and ownership.
    public WorldSnapshot BeginReplication() =>
        Replication.Start(Host.CaptureSnapshot());

    public WorldDelta? CaptureDelta() =>
        Replication.CaptureChanges(Host.CaptureSnapshot());

    private LoadedSingleMapHost(RimWorldSingleMapDriver driver,
        AuthoritativeSimulationHost host)
    {
        Driver = driver;
        Host = host;
    }

    public static LoadedSingleMapHost Attach(Map map, int factionId,
        ICommandAuthorization authorization)
    {
        if (map == null) throw new ArgumentNullException(nameof(map));
        if (authorization == null) throw new ArgumentNullException(nameof(authorization));
        if (factionId <= 0) throw new ArgumentOutOfRangeException(nameof(factionId));

        var driver = new RimWorldSingleMapDriver(map);
        var host = new AuthoritativeSimulationHost(
            authorization, driver, () => driver.StateVersion);

        // The map may already have ticks in a loaded save; the server clock
        // must begin from the actual RimWorld state, not from arbitrary zero.
        host.Domains.AddDomain(driver.DomainId, factionId, Find.TickManager.TicksGame);
        // Remains paused until the caller explicitly sets the domain speed.
        return new LoadedSingleMapHost(driver, host);
    }
}
