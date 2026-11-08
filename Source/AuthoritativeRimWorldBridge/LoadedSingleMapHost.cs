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
