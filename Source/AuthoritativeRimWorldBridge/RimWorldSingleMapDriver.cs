using System;
using System.Linq;
using System.Threading;
using Multiplayer.Authoritative;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Authoritative.RimWorldBridge;

// Real Verse adapter for a loaded, paused, single-map RimWorld 1.6 game.
// It is intentionally NOT an automatic server bootstrap, headless launcher,
// authenticated transport, or complete game-state replicator.
public sealed class RimWorldSingleMapDriver : IAuthoritativeGameDriver
{
    private readonly Map map;
    private readonly int domainId;
    private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
    private readonly GameComponent_AuthoritativeIdentity identities;
    private long revision;

    public int DomainId => domainId;
    public long StateVersion => revision;
    public Guid WorldId { get { EnsureReady(); return identities.WorldId; } }

    public RimWorldSingleMapDriver(Map map)
    {
        this.map = map ?? throw new ArgumentNullException(nameof(map));
        domainId = MapDomainIds.FromMapUniqueId(map.uniqueID);
        EnsureReady();
        identities = GameComponent_AuthoritativeIdentity.ForCurrentGame();
        _ = identities.WorldId; // Fail closed before enabling a corrupt loaded world.
    }

    public void ExecuteGameTick(int requestedDomainId, long nextTick)
    {
        EnsureReady();
        if (requestedDomainId != domainId)
            throw new InvalidOperationException("Attempted to tick a different map domain");

        var tickManager = Find.TickManager;
        if (nextTick != (long)tickManager.TicksGame + 1)
            throw new InvalidOperationException("Domain clock diverged from RimWorld's game clock");

        // Actual RimWorld game simulation: standard Verse world + map tick
        // on the same Unity thread. This is intentionally single-map only.
        tickManager.DoSingleTick();
        if (tickManager.TicksGame != nextTick)
            throw new InvalidOperationException("RimWorld did not advance its game tick as expected");

        revision = checked(revision + 1);
    }

    public bool TryExecute(PlayerCommand command, out string reason)
    {
        EnsureReady();
        if (command.DomainId != domainId)
            return Reject("Command targets a different map domain", out reason);
        if (command.Kind != AuthoritativeCommandKind.MovePawn)
            return Reject("Only MovePawn is supported by the Verse bridge", out reason);

        // A command can resolve only a previously registered server identity.
        // Lookup never allocates IDs and never falls back to thingIDNumber.
        Pawn? pawn = null;
        foreach (var candidate in map.mapPawns.AllPawnsSpawned)
        {
            if (!candidate.Spawned || candidate.Map != map || candidate.Destroyed) continue;
            if (!identities.TryGetId(candidate, out var id) || id != command.TargetThingId) continue;
            if (pawn is not null)
                return Reject("Ambiguous server pawn identity", out reason);
            pawn = candidate;
        }
        if (pawn == null || pawn.Dead || pawn.Faction == null || pawn.Faction.loadID != command.FactionId)
            return Reject("Pawn does not exist or is not owned by this faction", out reason);

        var destination = new IntVec3(command.X, 0, command.Z);
        if (!destination.InBounds(map) || !destination.Walkable(map))
            return Reject("Destination is outside the map or not walkable", out reason);
        if (pawn.jobs == null)
            return Reject("Pawn cannot receive jobs", out reason);

        // This issues the genuine job to Verse; movement will then be performed
        // by the game's own simulation ticks. No client-side gameplay is run.
        var job = JobMaker.MakeJob(JobDefOf.Goto, destination);
        if (!pawn.jobs.TryTakeOrderedJob(job))
            return Reject("Verse refused the Goto order", out reason);
        revision = checked(revision + 1);
        reason = "Job submitted";
        return true;
    }

    public VerseContentManifestInputs CaptureManifestInputs()
    {
        EnsureReady();
        return VerseContentManifestInputs.CaptureOnGameThread();
    }

    // The world ID comes from the server's persisted identity component.
    // PlanetTile conversion and actual active-content hashing remain explicit.
    public WorldBootstrapV2 CaptureBootstrapV2(
        int worldTile, ContentManifest authoritativeManifest, long networkVersion) =>
        CaptureBootstrapV2(WorldId, worldTile, authoritativeManifest, networkVersion);

    // Preserve the previous call shape, but reject arbitrary caller world IDs.
    public WorldBootstrapV2 CaptureBootstrapV2(
        Guid worldId, int worldTile, ContentManifest authoritativeManifest,
        long networkVersion)
    {
        EnsureReady();
        if (worldId != identities.WorldId)
            throw new InvalidOperationException("Bootstrap world identity does not match the loaded server save");
        var source = new VerseSingleMapBootstrapSource(map, worldTile);
        return WorldBootstrapCapture.Capture(
            worldId, networkVersion, StateVersion,
            authoritativeManifest, new IWorldBootstrapMapSource[] { source });
    }

    // The v1 test projection and v2 bootstrap share one server identity registry.
    // This still is NOT a complete client-map snapshot or reconnect checkpoint.
    public WorldSnapshot CaptureSnapshot()
    {
        EnsureReady();
        var things = map.mapPawns.AllPawnsSpawned
            .Where(p => p.Spawned && p.Map == map && !p.Destroyed &&
                        p.Faction != null && p.Faction.loadID > 0)
            .OrderBy(p => p.thingIDNumber)
            .Select(p => new ThingState(
                identities.GetOrAssignId(p), domainId, p.Faction.loadID, p.Position.x, p.Position.z))
            .OrderBy(p => p.Id)
            .ToArray();
        return new WorldSnapshot(revision, things);
    }

    private static bool Reject(string why, out string reason)
    {
        reason = why;
        return false;
    }

    private void EnsureReady()
    {
        if (Thread.CurrentThread.ManagedThreadId != ownerThread)
            throw new InvalidOperationException("Verse bridge requires the creating Unity thread");
        if (Current.ProgramState != ProgramState.Playing || Current.Game == null ||
            Find.TickManager == null || Find.Maps.Count != 1 || !ReferenceEquals(Find.Maps[0], map))
            throw new InvalidOperationException("Verse bridge requires an active game with exactly one loaded map");
        if (Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
            throw new InvalidOperationException("Pause vanilla time before invoking the authoritative driver");
    }
}
