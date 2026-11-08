using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Multiplayer.Authoritative;
using Verse;

namespace Multiplayer.Authoritative.RimWorldBridge;

// Experimental server-only read adapter. Nothing here spawns Things, changes
// ownership, runs jobs or enables remote access. Must run on the Unity owner
// thread with exactly one loaded map and vanilla time paused.
public static class VerseWorldViewCapture
{
    public static WorldViewCheckpoint Capture(RimWorldSingleMapDriver driver, int worldTile,
        ContentManifest authoritativeManifest, long networkVersion)
    {
        if (driver is null) throw new ArgumentNullException(nameof(driver));
        // Driver checks Unity thread, loaded game, pause and content metadata.
        var bootstrap = driver.CaptureBootstrapV2(worldTile, authoritativeManifest, networkVersion);
        var map = Find.Maps[0];
        if (MapDomainIds.FromMapUniqueId(map.uniqueID) != driver.DomainId)
            throw new InvalidOperationException("Graph map differs from the driver's domain");
        var identities = GameComponent_AuthoritativeIdentity.ForCurrentGame();
        if (identities.WorldId != bootstrap.WorldId)
            throw new InvalidOperationException("Loaded world changed during view capture");
        var graph = new Collector(map, identities).Capture(bootstrap);
        if (driver.StateVersion != bootstrap.GameStateVersion || driver.WorldId != bootstrap.WorldId ||
            Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
            throw new InvalidOperationException("Server state changed during view capture");
        return WorldViewCheckpointValidator.ValidateAndCopy(new(bootstrap, graph));
    }

    private sealed class Collector
    {
        private readonly Map map;
        private readonly int domainId;
        private readonly GameComponent_AuthoritativeIdentity identities;
        private readonly Queue<(Thing Thing, long Owner, EntityHolderSlot Slot, int Depth)> pending = new();
        private readonly HashSet<Thing> seen = new(new ThingReferenceComparer());
        private readonly List<EntityGraphNode> nodes = new();
        private readonly List<EntityHolder> holders = new();

        public Collector(Map map, GameComponent_AuthoritativeIdentity identities)
        {
            this.map = map;
            this.identities = identities;
            domainId = MapDomainIds.FromMapUniqueId(map.uniqueID);
        }

        public EntityGraphSnapshot Capture(WorldBootstrapV2 bootstrap)
        {
            if (map.listerThings.AllThings.Count > EntityGraphFormat.MaxEntities)
                throw new InvalidOperationException("Spawned Thing budget exceeded");
            foreach (var thing in map.listerThings.AllThings.Where(t => t != null && t.Spawned && t.Map == map)
                         .OrderBy(t => t.thingIDNumber))
                Enqueue(thing, 0, EntityHolderSlot.None, 0);

            while (pending.Count != 0)
            {
                var entry = pending.Dequeue();
                var thing = entry.Thing;
                var id = identities.GetOrAssignId(thing);
                var pawn = thing as Pawn;
                var payload = pawn is null ? null : new PawnPresentation(
                    pawn.Drafted, pawn.Downed, pawn.Dead, pawn.CurJob?.def?.defName);
                var entity = new EntityPresentation(id, domainId, thing.Faction?.loadID ?? 0,
                    thing.def?.defName ?? throw new InvalidOperationException("Thing has no Def"),
                    entry.Owner == 0 ? thing.Position.x : -1,
                    entry.Owner == 0 ? thing.Position.z : -1,
                    thing.Rotation.AsInt, thing.stackCount,
                    pawn is null ? PresentationKind.Thing : PresentationKind.Pawn, payload);
                nodes.Add(new(entity, new(entry.Owner, entry.Slot)));
                ReadHolders(thing, id, entry.Depth);
            }
            return EntityGraphValidator.ValidateAndCopy(new(EntityGraphFormat.Version, bootstrap.WorldId,
                bootstrap.NetworkVersion, bootstrap.GameStateVersion,
                new[] { new EntityGraphMap(domainId, map.Size.x, map.Size.z) }, nodes, holders));
        }

        private void Enqueue(Thing thing, long owner, EntityHolderSlot slot, int depth)
        {
            if (thing is null || thing.Destroyed || depth > EntityGraphFormat.MaxHolderDepth ||
                (owner == 0 ? !thing.Spawned || thing.Map != map : thing.Spawned))
                throw new InvalidOperationException("Invalid or excessively nested server Thing");
            if (seen.Count >= EntityGraphFormat.MaxEntities || !seen.Add(thing))
                throw new InvalidOperationException("Thing is duplicated, cyclic or exceeds the graph budget");
            pending.Enqueue((thing, owner, slot, depth));
        }

        private void ReadHolders(Thing owner, long ownerId, int depth)
        {
            var known = new List<IThingHolder>();
            var knownContainers = new List<ThingOwner>();
            var children = new HashSet<Thing>(new ThingReferenceComparer());

            void AddDirect(IThingHolder holder, EntityHolderSlot slot, ThingOwner container)
            {
                if (holders.Count >= EntityGraphFormat.MaxHolders ||
                    container.Count > EntityGraphFormat.MaxChildrenPerHolder)
                    throw new InvalidOperationException("Server holder budget exceeded");
                holders.Add(new(ownerId, slot));
                known.Add(holder);
                knownContainers.Add(container);
                for (var i = 0; i < container.Count; i++)
                {
                    var child = container[i];
                    if (child is null || !ReferenceEquals(child.ParentHolder, holder))
                        throw new InvalidOperationException("ThingOwner and ParentHolder disagree");
                    children.Add(child);
                    Enqueue(child, ownerId, slot, depth + 1);
                }
            }

            void AddTracker(object? tracker, EntityHolderSlot slot)
            {
                if (tracker is null) return;
                if (tracker is not IThingHolder holder)
                    throw new NotSupportedException("Pawn tracker does not expose a Thing holder");
                var container = holder.GetDirectlyHeldThings()
                    ?? throw new NotSupportedException("Pawn tracker has no direct container");
                AddDirect(holder, slot, container);
            }

            if (owner is Pawn pawn)
            {
                AddTracker(pawn.inventory, EntityHolderSlot.Inventory);
                AddTracker(pawn.equipment, EntityHolderSlot.Equipment);
                AddTracker(pawn.apparel, EntityHolderSlot.Apparel);
                AddTracker(pawn.carryTracker, EntityHolderSlot.Carry);
            }

            if (owner is IThingHolder root)
            {
                var direct = root.GetDirectlyHeldThings();
                if (owner is Pawn)
                {
                    if (direct is not null && direct.Count != 0 && !knownContainers.Any(c => ReferenceEquals(c, direct)))
                        throw new NotSupportedException("Pawn has an unrepresented direct holder");
                }
                else if (direct is not null)
                    AddDirect(root, EntityHolderSlot.Contents, direct);

                var advertised = new List<IThingHolder>();
                root.GetChildHolders(advertised);
                if (advertised.Count > EntityGraphFormat.MaxChildrenPerHolder)
                    throw new InvalidOperationException("Too many advertised server holders");
                foreach (var childHolder in advertised)
                {
                    // Nested Thing holders are visited as their own graph entities.
                    if (known.Any(h => ReferenceEquals(h, childHolder)) ||
                        (childHolder is Thing childThing && children.Contains(childThing))) continue;
                    throw new NotSupportedException("An additional holder needs an explicit graph adapter: " +
                        childHolder?.GetType().FullName);
                }
            }

            // Do not silently discard inventory stored by a mod's ThingComp.
            // Such component-only holders need explicit slot semantics later.
            if (owner is ThingWithComps withComps)
                foreach (var comp in withComps.AllComps)
                    if (comp is IThingHolder compHolder && !known.Any(h => ReferenceEquals(h, compHolder)))
                        throw new NotSupportedException("Component-only holder requires an adapter: " + comp.GetType().FullName);
        }
    }

    private sealed class ThingReferenceComparer : IEqualityComparer<Thing>
    {
        public bool Equals(Thing? x, Thing? y) => ReferenceEquals(x, y);
        public int GetHashCode(Thing obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
