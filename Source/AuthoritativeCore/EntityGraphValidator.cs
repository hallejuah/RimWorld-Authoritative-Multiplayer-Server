using System.Text;

namespace Multiplayer.Authoritative;

// Validates a complete candidate BEFORE it can replace a client view. Chains
// are resolved iteratively with memoized depths: no recursive graph traversal.
public static class EntityGraphValidator
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    public static EntityGraphSnapshot ValidateAndCopy(EntityGraphSnapshot graph)
    {
        if (graph is null) throw new ArgumentNullException(nameof(graph));
        if (graph.FormatVersion != EntityGraphFormat.Version || graph.WorldId == Guid.Empty ||
            graph.NetworkVersion < 0 || graph.GameStateVersion < 0 || graph.Maps is null ||
            graph.Maps.Count == 0 || graph.Maps.Count > WorldBootstrapFormat.MaxMaps ||
            graph.Nodes is null || graph.Nodes.Count > EntityGraphFormat.MaxEntities ||
            graph.Holders is null || graph.Holders.Count > EntityGraphFormat.MaxHolders)
            throw Invalid("Invalid entity graph header or collection budget");

        var maps = new Dictionary<int, EntityGraphMap>();
        foreach (var map in graph.Maps)
        {
            if (map is null || map.DomainId <= 0 || map.Width <= 0 || map.Height <= 0 ||
                map.Width > WorldBootstrapFormat.MaxMapSide || map.Height > WorldBootstrapFormat.MaxMapSide ||
                maps.ContainsKey(map.DomainId))
                throw Invalid("Invalid or duplicate graph map");
            maps.Add(map.DomainId, map);
        }

        var nodes = new Dictionary<long, EntityGraphNode>();
        foreach (var node in graph.Nodes)
        {
            if (node is null || node.Entity is null || node.Attachment is null)
                throw Invalid("Missing node presentation or location");
            var e = node.Entity;
            var a = node.Attachment;
            if (e.Id <= 0 || nodes.ContainsKey(e.Id) || !maps.TryGetValue(e.DomainId, out var map) ||
                e.FactionId < 0 || !SafeName(e.DefName) || e.Rotation < 0 || e.Rotation > 3 ||
                e.StackCount <= 0 || e.StackCount > 1_000_000 ||
                e.Kind is not (PresentationKind.Thing or PresentationKind.Pawn) ||
                (e.Kind == PresentationKind.Pawn) != (e.Pawn is not null) ||
                (e.Pawn is not null && e.StackCount != 1) ||
                (e.Pawn?.CurrentJobDef is { } job && !SafeName(job)))
                throw Invalid("Invalid entity presentation or duplicate identity");
            if (a.HolderEntityId == 0)
            {
                if (a.Slot != EntityHolderSlot.None || e.X < 0 || e.X >= map.Width ||
                    e.Z < 0 || e.Z >= map.Height)
                    throw Invalid("Invalid map-root location");
            }
            else if (a.HolderEntityId < 0 || a.HolderEntityId == e.Id || !ValidSlot(a.Slot) ||
                     e.X != -1 || e.Z != -1)
                throw Invalid("Invalid held-entity location");
            nodes.Add(e.Id, node);
        }

        var holders = new Dictionary<(long, EntityHolderSlot), int>();
        foreach (var holder in graph.Holders)
        {
            if (holder is null || !ValidSlot(holder.Slot) ||
                !nodes.TryGetValue(holder.OwnerEntityId, out var owner))
                throw Invalid("Unknown holder owner or slot");
            var pawnSlot = holder.Slot != EntityHolderSlot.Contents;
            if (pawnSlot != (owner.Entity.Kind == PresentationKind.Pawn))
                throw Invalid("Holder slot is incompatible with its owner's kind");
            var key = (holder.OwnerEntityId, holder.Slot);
            if (holders.ContainsKey(key)) throw Invalid("Duplicate holder declaration");
            holders.Add(key, 0);
        }

        foreach (var node in nodes.Values)
        {
            var a = node.Attachment;
            if (a.HolderEntityId == 0) continue;
            var key = (a.HolderEntityId, a.Slot);
            if (!nodes.TryGetValue(a.HolderEntityId, out var parent) ||
                !holders.TryGetValue(key, out var count) || parent.Entity.DomainId != node.Entity.DomainId)
                throw Invalid("Dangling holder reference or cross-domain containment");
            if (a.Slot is EntityHolderSlot.Inventory or EntityHolderSlot.Equipment or EntityHolderSlot.Apparel)
            {
                if (node.Entity.Kind != PresentationKind.Thing)
                    throw Invalid("A pawn cannot occupy inventory, equipment or apparel slots");
                if (a.Slot != EntityHolderSlot.Inventory && node.Entity.StackCount != 1)
                    throw Invalid("Equipped/worn things must be single items");
            }
            count++;
            if (count > EntityGraphFormat.MaxChildrenPerHolder || (a.Slot == EntityHolderSlot.Carry && count > 1))
                throw Invalid("Holder child budget exceeded");
            holders[key] = count;
        }

        var depths = new Dictionary<long, int>();
        foreach (var start in nodes.Keys)
        {
            if (depths.ContainsKey(start)) continue;
            var path = new List<long>();
            var seen = new HashSet<long>();
            var cursor = start;
            int depth;
            while (!depths.TryGetValue(cursor, out depth))
            {
                if (!seen.Add(cursor)) throw Invalid("Containment cycle");
                var parent = nodes[cursor].Attachment.HolderEntityId;
                if (parent == 0)
                {
                    depth = 0;
                    depths.Add(cursor, depth);
                    break;
                }
                path.Add(cursor);
                if (path.Count > EntityGraphFormat.MaxHolderDepth)
                    throw Invalid("Containment depth budget exceeded");
                cursor = parent;
            }
            for (var i = path.Count - 1; i >= 0; i--)
            {
                if (++depth > EntityGraphFormat.MaxHolderDepth)
                    throw Invalid("Containment depth budget exceeded");
                depths.Add(path[i], depth);
            }
        }

        // Records contain only immutable scalar/record values; freeze all lists.
        return graph with
        {
            Maps = Array.AsReadOnly(maps.Values.OrderBy(m => m.DomainId).ToArray()),
            Nodes = Array.AsReadOnly(nodes.Values.OrderBy(n => n.Entity.Id).ToArray()),
            Holders = Array.AsReadOnly(graph.Holders.OrderBy(h => h.OwnerEntityId).ThenBy(h => h.Slot).ToArray())
        };
    }

    internal static bool ValidSlot(EntityHolderSlot slot) =>
        slot >= EntityHolderSlot.Inventory && slot <= EntityHolderSlot.Contents;

    internal static bool SafeName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name!.Length > 128 ||
            name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return false;
        try { return Utf8.GetByteCount(name) <= EntityGraphFormat.MaxNameBytes; }
        catch (EncoderFallbackException) { return false; }
    }

    private static ArgumentException Invalid(string message) => new(message, "graph");
}
