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
    private long revision;

    public int DomainId => domainId;
    public long StateVersion => revision;

    public RimWorldSingleMapDriver(Map map)
    {
        this.map = map ?? throw new ArgumentNullException(nameof(map));
        domainId = MapDomainIds.FromMapUniqueId(map.uniqueID);
        EnsureReady();
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

        // Speed is owned by the scheduler and must never be acknowledged by
        // a driver which has not actually changed the scheduler state.
        if (command.Kind != AuthoritativeCommandKind.MovePawn)
            return Reject("Only MovePawn is supported by the Verse bridge", out reason);

        var pawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p =>
            p.thingIDNumber == command.TargetThingId);
        if (pawn == null || !pawn.Spawned || pawn.Map != map || pawn.Dead ||
            pawn.Faction == null || pawn.Faction.loadID != command.FactionId)
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

    // Capture the actual active mod roots, their About.xml version metadata
    // and resolved Def database identities on the Unity thread. Disk hashing
    // of the copied descriptors should be off-thread, before activating v2.
    public VerseContentManifestInputs CaptureManifestInputs()
    {
        EnsureReady();
        return VerseContentManifestInputs.CaptureOnGameThread();
    }

    // A proper future session must persist worldId and derive the manifest
    // from actual loaded mods/Defs, never synthesize hashes or accept them
    // from an untrusted client. RimWorld 1.6 PlanetTile conversion is left
    // explicit; blindly hashing Map.Tile is not a stable global tile ID.
    public WorldBootstrapV2 CaptureBootstrapV2(
        Guid worldId, int worldTile, ContentManifest authoritativeManifest,
        long networkVersion)
    {
        EnsureReady();
        var source = new VerseSingleMapBootstrapSource(map, worldTile);
        return WorldBootstrapCapture.Capture(
            worldId, networkVersion, StateVersion,
            authoritativeManifest, new IWorldBootstrapMapSource[] { source });
    }

    // Deliberately small proof-of-concept projection, NOT a RimWorld snapshot
    // suitable for constructing a complete client map or reconnecting.
    public WorldSnapshot CaptureSnapshot()
    {
        EnsureReady();
        var things = map.mapPawns.AllPawnsSpawned
            .Where(p => p.Spawned && p.Faction != null && p.Faction.loadID > 0 &&
                        p.thingIDNumber > 0)
            .OrderBy(p => p.thingIDNumber)
            .Select(p => new ThingState(
                p.thingIDNumber, domainId, p.Faction.loadID, p.Position.x, p.Position.z))
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

        // Vanilla TickManagerUpdate must not advance simulation independently.
        // Keeping the normal game's speed paused is the preliminary safety gate.
        // A real dedicated server should replace this with a verified patch.
        if (Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
            throw new InvalidOperationException("Pause vanilla time before invoking the authoritative driver");
    }
}
