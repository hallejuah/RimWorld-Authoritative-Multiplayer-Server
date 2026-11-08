using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Multiplayer.Authoritative;
using Verse;

namespace Multiplayer.Authoritative.RimWorldBridge;

// Read-only, game-thread adapter for an already loaded RimWorld 1.6 map.
// A separate trusted host must resolve Map.Tile (PlanetTile in 1.6) to the
// v2 integer tile coordinate, and must provide a REAL content fingerprint.
// No game logic, Thing spawning, map generation or Scribe save occurs here.
public sealed class VerseSingleMapBootstrapSource : IWorldBootstrapMapSource
{
    private readonly Map map;
    private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;

    public int DomainId { get; }
    public int WorldTile { get; }
    public int Width => map.Size.x;
    public int Height => map.Size.z;

    public VerseSingleMapBootstrapSource(Map map, int worldTile)
    {
        this.map = map ?? throw new ArgumentNullException(nameof(map));
        if (worldTile < 0)
            throw new ArgumentOutOfRangeException(nameof(worldTile));
        WorldTile = worldTile;
        DomainId = MapDomainIds.FromMapUniqueId(map.uniqueID);
        EnsureReady();
    }

    public string TerrainDefAt(int x, int z)
    {
        EnsureReady();
        CheckBounds(x, z);
        return map.terrainGrid.TerrainAt(new IntVec3(x, 0, z)).defName
            ?? throw new InvalidOperationException("Missing active terrain Def");
    }

    public string? RoofDefAt(int x, int z)
    {
        EnsureReady();
        CheckBounds(x, z);
        return map.roofGrid.RoofAt(new IntVec3(x, 0, z))?.defName;
    }

    public bool FoggedAt(int x, int z)
    {
        EnsureReady();
        CheckBounds(x, z);
        return map.fogGrid.IsFogged(new IntVec3(x, 0, z));
    }

    public IReadOnlyList<EntityPresentation> ReadSpawnedEntities()
    {
        EnsureReady();
        var entities = new List<EntityPresentation>();
        var ids = new HashSet<long>();
        foreach (var thing in map.listerThings.AllThings)
        {
            if (thing == null || !thing.Spawned || thing.Map != map)
                continue;
            if (thing.thingIDNumber <= 0)
                throw new InvalidOperationException("A spawned Thing has no positive identity");

            var id = (long)thing.thingIDNumber;
            if (!ids.Add(id))
                throw new InvalidOperationException("Duplicate spawned Thing identity");

            var position = thing.Position;
            if (position.x < 0 || position.x >= Width ||
                position.z < 0 || position.z >= Height)
                throw new InvalidOperationException("Spawned Thing is outside map bounds");

            var pawn = thing as Pawn;
            var payload = pawn is null ? null : new PawnPresentation(
                pawn.Drafted,
                pawn.Downed,
                pawn.Dead,
                pawn.CurJob?.def?.defName);
            var faction = thing.Faction;
            if (faction != null && faction.loadID <= 0)
                throw new InvalidOperationException("Spawned Thing faction has invalid ID");

            entities.Add(new EntityPresentation(
                id,
                DomainId,
                faction?.loadID ?? 0,
                thing.def?.defName ?? throw new InvalidOperationException("Thing lacks Def"),
                position.x, position.z,
                thing.Rotation.AsInt,
                thing.stackCount,
                pawn is null ? PresentationKind.Thing : PresentationKind.Pawn,
                payload));
        }
        return entities;
    }

    private void CheckBounds(int x, int z)
    {
        if (x < 0 || x >= Width || z < 0 || z >= Height)
            throw new ArgumentOutOfRangeException(nameof(x));
    }

    private void EnsureReady()
    {
        if (Thread.CurrentThread.ManagedThreadId != ownerThread)
            throw new InvalidOperationException("Map extraction must run on the Unity owner thread");
        if (Current.ProgramState != ProgramState.Playing || Current.Game == null ||
            Find.TickManager == null || Find.Maps.Count != 1 ||
            !ReferenceEquals(Find.Maps[0], map) ||
            Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
            throw new InvalidOperationException("A single loaded and paused game map is required");
    }
}
