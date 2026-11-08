using System.Buffers.Binary;
using System.Security.Cryptography;
using Multiplayer.Authoritative;

var cases = new (string Name, Action Run)[]
{
    ("Pawn equipment, inventory, apparel, carry and nested contents roundtrip", Tests.Roundtrip),
    ("Graph encoding is canonical under reordered collections", Tests.Canonical),
    ("Entity identities and holder declarations are unique", Tests.Duplicates),
    ("Missing holder owners and slots are rejected", Tests.Dangling),
    ("Every entity has exactly one canonical location", Tests.Locations),
    ("Containment keeps domains consistent without inheriting factions", Tests.Domains),
    ("Holder kinds and equipment cardinality are validated", Tests.Slots),
    ("Self cycles and multi-node containment cycles fail", Tests.Cycles),
    ("Depth limit is enforced independently of enumeration order", Tests.Depth),
    ("Map, entity and holder budgets fail before acceptance", Tests.Budgets),
    ("Pawn flags, stack sizes, Def names and encodings are validated", Tests.Presentations),
    ("Encoded graph byte budget is enforced", Tests.EncodedBudget),
    ("Truncation, corruption and foreign worlds fail closed", Tests.Corruption),
    ("Valid checksums do not bypass structural validation", Tests.MalformedBinary),
    ("Validated graph collections cannot mutate accepted state", Tests.DefensiveCopies),
    ("Map and graph checkpoints must describe the same roots and revision", Tests.CheckpointConsistency),
    ("Client commits both checkpoint parts or neither", Tests.AtomicInstall),
    ("Client pins world, content and monotonic revisions", Tests.Admission),
    ("Identical retries succeed but same-revision changes fail", Tests.RevisionConflicts),
    ("Server view can move one item between map and holders without duplicates", Tests.TransferProjection),
    ("World view state is presentation-thread affine", Tests.ThreadAffinity),
    ("Randomized forests preserve containment after serialization", Tests.RandomForests)
};
var failures = 0;
foreach (var test in cases)
{
    try { test.Run(); Console.WriteLine("PASS: " + test.Name); }
    catch (Exception e) { failures++; Console.Error.WriteLine("FAIL: " + test.Name + ": " + e); }
}
Console.WriteLine($"{cases.Length - failures}/{cases.Length} entity graph tests passed");
return failures == 0 ? 0 : 1;

internal static class Tests
{
    private static readonly Guid World = Guid.Parse("48c0ea24-fd1a-4590-88ea-6f99c8dbce68");
    private static ContentManifest Manifest => new("1.6.4566",
        [new("ludeon.rimworld", "1.6", new string('a', 64))], new string('b', 64));

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static EntityGraphNode Item(long id, long owner = 0, EntityHolderSlot slot = EntityHolderSlot.None,
        string def = "Steel", int count = 1) => new(
        new(id, 10, 0, def, owner == 0 ? 1 : -1, owner == 0 ? 2 : -1, 0, count, PresentationKind.Thing, null),
        new(owner, slot));

    private static EntityGraphNode Pawn(long id, long owner = 0, EntityHolderSlot slot = EntityHolderSlot.None,
        int faction = 1) => new(
        new(id, 10, faction, "Human", owner == 0 ? 1 : -1, owner == 0 ? 2 : -1, 0, 1,
            PresentationKind.Pawn, new(false, owner != 0, false, "Wait")), new(owner, slot));

    private static EntityGraphSnapshot Graph() => new(EntityGraphFormat.Version, World, 10, 20,
        [new(10, 20, 20)],
        [Pawn(1), Item(2, 1, EntityHolderSlot.Equipment, "Gun"), Item(3, 1, EntityHolderSlot.Apparel, "Shirt"),
         Item(4, 1, EntityHolderSlot.Inventory, "Medicine", 10), Pawn(5, 1, EntityHolderSlot.Carry, 2),
         Item(6, 1, EntityHolderSlot.Inventory, "Bag"), Item(7, 6, EntityHolderSlot.Contents, "Meal", 5), Item(8)],
        [new(1, EntityHolderSlot.Inventory), new(1, EntityHolderSlot.Equipment), new(1, EntityHolderSlot.Apparel),
         new(1, EntityHolderSlot.Carry), new(5, EntityHolderSlot.Inventory), new(6, EntityHolderSlot.Contents)]);

    private static EntityGraphSnapshot Change(EntityGraphSnapshot graph, long id, Func<EntityGraphNode, EntityGraphNode> edit) =>
        graph with { Nodes = graph.Nodes.Select(n => n.Entity.Id == id ? edit(n) : n).ToArray() };

    private static void Reject(EntityGraphSnapshot graph) =>
        Throws<ArgumentException>(() => EntityGraphValidator.ValidateAndCopy(graph));

    private static WorldViewCheckpoint Pair(EntityGraphSnapshot graph)
    {
        var maps = graph.Maps.Select(m => new MapLayerSnapshot(m.DomainId, m.DomainId, m.Width, m.Height,
            ["Soil"], new byte[m.Width * m.Height], [], new byte[m.Width * m.Height],
            new byte[(m.Width * m.Height + 7) / 8])).ToArray();
        var roots = graph.Nodes.Where(n => n.Attachment.HolderEntityId == 0).Select(n => n.Entity).ToArray();
        return new(new(WorldBootstrapFormat.Version, graph.WorldId, graph.NetworkVersion, graph.GameStateVersion,
            Manifest, maps, roots), graph);
    }

    public static void Roundtrip()
    {
        var g = EntityGraphCodec.Decode(EntityGraphCodec.Encode(Graph()), World);
        Check(g.Nodes.Count == 8 && g.Holders.Count == 6, "lost graph contents");
        Check(g.Nodes.Single(n => n.Entity.Id == 7).Attachment.HolderEntityId == 6, "nested holder lost");
        Check(g.Nodes.Single(n => n.Entity.Id == 5).Entity.Pawn!.Downed, "held pawn state lost");
        Check(g.Nodes.Count(n => n.Attachment.HolderEntityId == 0) == 2, "held item became map root");
        Check(WorldViewCheckpointValidator.ValidateAndCopy(Pair(g)).Graph.Nodes.Count == 8, "pair rejected");
        var empty = g with { Nodes = [], Holders = [] };
        Check(EntityGraphCodec.Decode(EntityGraphCodec.Encode(empty), World).Nodes.Count == 0, "empty map rejected");
    }

    public static void Canonical()
    {
        var g = Graph();
        var changed = g with { Nodes = g.Nodes.Reverse().ToArray(), Holders = g.Holders.Reverse().ToArray() };
        Check(EntityGraphCodec.Encode(g).SequenceEqual(EntityGraphCodec.Encode(changed)), "enumeration changed bytes");
        var multi = g with { Maps = [new(20, 8, 8), g.Maps[0]] };
        Check(EntityGraphCodec.Encode(multi).SequenceEqual(EntityGraphCodec.Encode(multi with
            { Maps = multi.Maps.Reverse().ToArray() })), "map ordering changed bytes");
    }

    public static void Duplicates()
    {
        var g = Graph();
        Reject(g with { Nodes = g.Nodes.Append(g.Nodes[0]).ToArray() });
        Reject(g with { Holders = g.Holders.Append(g.Holders[0]).ToArray() });
        Reject(g with { Maps = [g.Maps[0], g.Maps[0]] });
    }

    public static void Dangling()
    {
        var g = Graph();
        Reject(Change(g, 7, n => n with { Attachment = new(999, EntityHolderSlot.Contents) }));
        Reject(g with { Holders = g.Holders.Where(h => h.OwnerEntityId != 6).ToArray() });
        Reject(g with { Holders = g.Holders.Append(new(999, EntityHolderSlot.Contents)).ToArray() });
        Reject(g with { Nodes = g.Nodes.Where(n => n.Entity.Id != 6).ToArray() });
    }

    public static void Locations()
    {
        var g = Graph();
        Reject(Change(g, 7, n => n with { Entity = n.Entity with { X = 1 } }));
        Reject(Change(g, 8, n => n with { Attachment = new(0, EntityHolderSlot.Inventory) }));
        Reject(Change(g, 8, n => n with { Entity = n.Entity with { X = -1, Z = -1 } }));
        Reject(Change(g, 8, n => n with { Entity = n.Entity with { X = 20 } }));
        Reject(Change(g, 7, n => n with { Attachment = new(-1, EntityHolderSlot.Contents) }));
    }

    public static void Domains()
    {
        var g = Graph() with { Maps = [new(10, 20, 20), new(20, 20, 20)] };
        Reject(Change(g, 7, n => n with { Entity = n.Entity with { DomainId = 20 } }));
        Reject(Change(g, 8, n => n with { Entity = n.Entity with { DomainId = 99 } }));
        var valid = EntityGraphValidator.ValidateAndCopy(g);
        Check(valid.Nodes.Single(n => n.Entity.Id == 5).Entity.FactionId == 2, "carried pawn inherited owner faction");
    }

    public static void Slots()
    {
        var g = Graph();
        Reject(g with { Holders = g.Holders.Append(new(6, EntityHolderSlot.Inventory)).ToArray() });
        Reject(g with { Holders = g.Holders.Append(new(1, EntityHolderSlot.Contents)).ToArray() });
        Reject(g with { Holders = g.Holders.Append(new(1, (EntityHolderSlot)99)).ToArray() });
        Reject(g with { Nodes = g.Nodes.Append(Item(9, 1, EntityHolderSlot.Carry)).ToArray() });
        Reject(Change(g, 2, n => n with { Entity = n.Entity with { StackCount = 2 } }));
        Reject(Change(g, 5, n => n with { Attachment = new(1, EntityHolderSlot.Inventory) }));
        Reject(Change(g, 7, n => n with { Attachment = new(6, EntityHolderSlot.None) }));
    }

    public static void Cycles()
    {
        var g = Graph();
        Reject(Change(g, 6, n => n with { Attachment = new(6, EntityHolderSlot.Contents) }));
        var cycle = g with { Holders = g.Holders.Append(new(7, EntityHolderSlot.Contents)).ToArray() };
        Reject(Change(cycle, 6, n => n with { Attachment = new(7, EntityHolderSlot.Contents) }));
    }

    private static EntityGraphSnapshot Chain(int depth)
    {
        return new(EntityGraphFormat.Version, World, 0, 0, [new(10, 20, 20)],
            Enumerable.Range(1, depth + 1).Select(i => i == 1 ? Item(1) : Item(i, i - 1, EntityHolderSlot.Contents)).ToArray(),
            Enumerable.Range(1, depth).Select(i => new EntityHolder(i, EntityHolderSlot.Contents)).ToArray());
    }

    public static void Depth()
    {
        var valid = Chain(EntityGraphFormat.MaxHolderDepth);
        EntityGraphValidator.ValidateAndCopy(valid);
        EntityGraphValidator.ValidateAndCopy(valid with { Nodes = valid.Nodes.Reverse().ToArray() });
        var invalid = Chain(EntityGraphFormat.MaxHolderDepth + 1);
        Reject(invalid);
        Reject(invalid with { Nodes = invalid.Nodes.Reverse().ToArray() });
    }

    public static void Budgets()
    {
        var g = Graph();
        Reject(g with { Nodes = new EntityGraphNode[EntityGraphFormat.MaxEntities + 1] });
        Reject(g with { Holders = new EntityHolder[EntityGraphFormat.MaxHolders + 1] });
        Reject(g with { Maps = [new(10, 513, 20)] });
        Reject(g with { Maps = [] });
        Reject(g with { WorldId = Guid.Empty });
        Reject(g with { NetworkVersion = -1 });
        Reject(g with { GameStateVersion = -1 });
        Reject(g with { FormatVersion = 99 });
        Reject(g with { Nodes = [null!] });
        Reject(g with { Holders = [null!] });
        var crowded = Chain(0) with
        {
            Nodes = new[] { Item(1) }.Concat(Enumerable.Range(2, EntityGraphFormat.MaxChildrenPerHolder + 1)
                .Select(i => Item(i, 1, EntityHolderSlot.Contents))).ToArray(),
            Holders = [new(1, EntityHolderSlot.Contents)]
        };
        Reject(crowded);
        EntityGraphValidator.ValidateAndCopy(crowded with { Nodes = crowded.Nodes.Take(crowded.Nodes.Count - 1).ToArray() });
    }

    public static void Presentations()
    {
        var g = Graph();
        foreach (var name in new[] { "", "has space", "bad\0name", new string('x', 129), "\ud800" })
            Reject(Change(g, 8, n => n with { Entity = n.Entity with { DefName = name } }));
        foreach (var count in new[] { 0, -1, 1_000_001 })
            Reject(Change(g, 8, n => n with { Entity = n.Entity with { StackCount = count } }));
        Reject(Change(g, 1, n => n with { Entity = n.Entity with { Pawn = null } }));
        Reject(Change(g, 1, n => n with { Entity = n.Entity with { StackCount = 2 } }));
        Reject(Change(g, 1, n => n with { Entity = n.Entity with { Pawn = n.Entity.Pawn! with { CurrentJobDef = "bad job" } } }));
        Reject(Change(g, 8, n => n with { Entity = n.Entity with { Rotation = 4 } }));
        Reject(Change(g, 8, n => n with { Entity = n.Entity with { FactionId = -1 } }));
        Reject(Change(g, 8, n => n with { Entity = n.Entity with { Kind = (PresentationKind)99 } }));
        var unicode = Change(g, 8, n => n with { Entity = n.Entity with { DefName = new string('\u0430', 128) } });
        EntityGraphCodec.Decode(EntityGraphCodec.Encode(unicode), World);
    }

    public static void EncodedBudget()
    {
        var g = Chain(0) with { Nodes = Enumerable.Range(1, 60_000)
            .Select(i => Item(i, def: new string('x', 128))).ToArray() };
        Throws<ArgumentException>(() => EntityGraphCodec.Encode(g));
        Throws<InvalidDataException>(() => EntityGraphCodec.Decode(new byte[EntityGraphFormat.MaxBytes + 1], World));
    }

    public static void Corruption()
    {
        var bytes = EntityGraphCodec.Encode(Graph());
        for (var i = 0; i < bytes.Length; i++)
        {
            Throws<InvalidDataException>(() => EntityGraphCodec.Decode(bytes[..i], World));
            var changed = (byte[])bytes.Clone();
            changed[i] ^= 1;
            Throws<InvalidDataException>(() => EntityGraphCodec.Decode(changed, World));
        }
        Throws<InvalidDataException>(() => EntityGraphCodec.Decode(bytes, Guid.NewGuid()));
        Throws<InvalidDataException>(() => EntityGraphCodec.Decode(bytes, Guid.Empty));
        Throws<InvalidDataException>(() => EntityGraphCodec.Decode(null!, World));
    }

    private static void Rehash(byte[] bytes) =>
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes, bytes.Length - 32);

    public static void MalformedBinary()
    {
        var simple = Chain(0) with { Nodes = [Item(1, def: "A")] };
        var bytes = EntityGraphCodec.Encode(simple);
        void Bad(Action<byte[]> mutate)
        {
            var changed = (byte[])bytes.Clone();
            mutate(changed);
            Rehash(changed);
            Throws<InvalidDataException>(() => EntityGraphCodec.Decode(changed, World));
        }
        Bad(b => b[0] = 0);
        Bad(b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 99));
        Bad(b => b[8] ^= 1);
        Bad(b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), -1));
        Bad(b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40), 17));
        Bad(b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(56), -1));
        Bad(b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(56), 100_001));
        Bad(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(76), 257));
        Bad(b => b[78] = 0xff);
        Bad(b => b[95] = 99);
        Bad(b => b[104] = (byte)EntityHolderSlot.Inventory);
        var trailing = new byte[bytes.Length + 1];
        Array.Copy(bytes, trailing, bytes.Length - 32);
        Rehash(trailing);
        Throws<InvalidDataException>(() => EntityGraphCodec.Decode(trailing, World));
        var pawnBytes = EntityGraphCodec.Encode(Graph());
        pawnBytes[100] = 8; // First node is Human pawn with a five-byte Def name.
        Rehash(pawnBytes);
        Throws<InvalidDataException>(() => EntityGraphCodec.Decode(pawnBytes, World));
    }

    public static void DefensiveCopies()
    {
        var g = Graph();
        var input = g.Nodes.ToArray();
        var clean = EntityGraphValidator.ValidateAndCopy(g with { Nodes = input });
        input[0] = Item(999);
        Check(clean.Nodes[0].Entity.Id == 1, "validator retained writable node collection");
        Throws<NotSupportedException>(() => ((IList<EntityGraphNode>)clean.Nodes)[0] = Item(999));
        Throws<NotSupportedException>(() => ((IList<EntityHolder>)clean.Holders).Clear());
        Throws<NotSupportedException>(() => ((IList<EntityGraphMap>)clean.Maps).Clear());
    }

    public static void CheckpointConsistency()
    {
        var p = Pair(Graph());
        void Bad(WorldViewCheckpoint bad) => Throws<ArgumentException>(() => WorldViewCheckpointValidator.ValidateAndCopy(bad));
        Bad(p with { Graph = p.Graph with { WorldId = Guid.NewGuid() } });
        Bad(p with { Graph = p.Graph with { NetworkVersion = 11 } });
        Bad(p with { Graph = p.Graph with { GameStateVersion = 21 } });
        Bad(p with { Graph = p.Graph with { Maps = [new(10, 21, 20)] } });
        Bad(p with { Graph = Change(p.Graph, 8, n => n with { Entity = n.Entity with { X = 3 } }) });
        Bad(p with { Bootstrap = p.Bootstrap with { Entities = [p.Bootstrap.Entities[0]] } });
        Bad(p with { Graph = p.Graph with { Nodes = p.Graph.Nodes.Append(Item(99)).ToArray() } });
        Bad(p with { Graph = null! });
    }

    public static void AtomicInstall()
    {
        var client = new ClientWorldViewReplica(Manifest, World);
        Check(client.NetworkVersion == -1, "new replica initialized");
        Throws<InvalidOperationException>(() => client.GetSnapshotCopy());
        var p = Pair(Graph());
        Check(client.TryInstall(p) == WorldViewInstallResult.Accepted, "initial pair rejected");
        var before = EntityGraphCodec.Encode(client.GetSnapshotCopy().Graph);
        var changed = Change(p.Graph, 4, n => n with { Entity = n.Entity with { StackCount = 999 } });
        changed = Change(changed, 7, n => n with { Attachment = new(999, EntityHolderSlot.Contents) });
        changed = changed with { NetworkVersion = 11, GameStateVersion = 21 };
        Check(client.TryInstall(Pair(changed)) == WorldViewInstallResult.InvalidState, "bad holder accepted");
        Check(client.TryInstall(null!) == WorldViewInstallResult.InvalidState, "null checkpoint accepted");
        Check(client.NetworkVersion == 10 && before.SequenceEqual(EntityGraphCodec.Encode(client.GetSnapshotCopy().Graph)),
            "invalid graph partially committed");
        p.Bootstrap.Maps[0].Terrain[0] = 250;
        var copy = client.GetSnapshotCopy();
        copy.Bootstrap.Maps[0].Terrain[0] = 200;
        Check(client.GetSnapshotCopy().Bootstrap.Maps[0].Terrain[0] == 0, "external map mutation leaked");
    }

    public static void Admission()
    {
        var c = new ClientWorldViewReplica(Manifest, World);
        c.TryInstall(Pair(Graph()));
        Check(c.TryInstall(Pair(Graph() with { WorldId = Guid.NewGuid() })) == WorldViewInstallResult.WrongWorld, "foreign world");
        var p = Pair(Graph() with { NetworkVersion = 11 });
        p = p with { Bootstrap = p.Bootstrap with { Manifest = Manifest with { DefCatalogSha256 = new string('c', 64) } } };
        Check(c.TryInstall(p) == WorldViewInstallResult.IncompatibleContent, "wrong content");
        Check(c.TryInstall(Pair(Graph() with { NetworkVersion = 9 })) == WorldViewInstallResult.StaleRevision, "network rollback");
        Check(c.TryInstall(Pair(Graph() with { NetworkVersion = 11, GameStateVersion = 19 })) == WorldViewInstallResult.StaleRevision, "game rollback");
        Check(c.NetworkVersion == 10, "rejected admission changed revision");
        Throws<ArgumentException>(() => new ClientWorldViewReplica(Manifest, Guid.Empty));
    }

    public static void RevisionConflicts()
    {
        var c = new ClientWorldViewReplica(Manifest, World);
        var g = Graph();
        c.TryInstall(Pair(g));
        Check(c.TryInstall(Pair(g with { Nodes = g.Nodes.Reverse().ToArray() })) == WorldViewInstallResult.Accepted, "exact retry failed");
        var changed = Change(g, 4, n => n with { Entity = n.Entity with { StackCount = 11 } });
        Check(c.TryInstall(Pair(changed)) == WorldViewInstallResult.RevisionConflict, "same-revision overwrite accepted");
        Check(c.TryInstall(Pair(g with { GameStateVersion = 21 })) == WorldViewInstallResult.RevisionConflict, "same revision new game state");
        Check(c.GetSnapshotCopy().Graph.Nodes.Single(n => n.Entity.Id == 4).Entity.StackCount == 10, "conflict committed");
    }

    public static void TransferProjection()
    {
        var c = new ClientWorldViewReplica(Manifest, World);
        var g = Graph();
        c.TryInstall(Pair(g));
        var moved = Change(g, 4, n => n with { Entity = n.Entity with { X = 3, Z = 4 }, Attachment = new(0, EntityHolderSlot.None) });
        moved = moved with { NetworkVersion = 11, GameStateVersion = 21 };
        Check(c.TryInstall(Pair(moved)) == WorldViewInstallResult.Accepted, "map transfer rejected");
        Check(c.GetSnapshotCopy().Bootstrap.Entities.Count == 3 && c.GetSnapshotCopy().Graph.Nodes.Count == 8, "transfer duplicated entity");
        var heldAgain = g with { NetworkVersion = 12, GameStateVersion = 22 };
        Check(c.TryInstall(Pair(heldAgain)) == WorldViewInstallResult.Accepted, "return to holder rejected");
        Check(c.GetSnapshotCopy().Bootstrap.Entities.Count == 2, "old root survived");
    }

    public static void ThreadAffinity()
    {
        var c = new ClientWorldViewReplica(Manifest, World);
        c.TryInstall(Pair(Graph()));
        Action[] calls = [() => c.TryInstall(Pair(Graph())), () => c.GetSnapshotCopy(), () => { _ = c.NetworkVersion; }];
        foreach (var call in calls)
        {
            Exception? error = null;
            var t = new Thread(() => { try { call(); } catch (Exception e) { error = e; } });
            t.Start(); t.Join();
            Check(error is InvalidOperationException, "foreign thread accessed world view");
        }
        Check(c.NetworkVersion == 10, "foreign thread mutated revision");
    }

    public static void RandomForests()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            var nodes = new List<EntityGraphNode> { Item(1) };
            for (var i = 2; i <= 300; i++)
                nodes.Add(i % 8 == 0 ? Item(i) : Item(i, random.Next(1, i), EntityHolderSlot.Contents));
            var g = new EntityGraphSnapshot(EntityGraphFormat.Version, World, seed, seed,
                [new(10, 20, 20)], nodes, nodes.Select(n => new EntityHolder(n.Entity.Id, EntityHolderSlot.Contents)).ToArray());
            var encoded = EntityGraphCodec.Encode(g);
            var shuffled = g with { Nodes = nodes.OrderBy(_ => random.Next()).ToArray(), Holders = g.Holders.Reverse().ToArray() };
            Check(encoded.SequenceEqual(EntityGraphCodec.Encode(shuffled)), "random canonical encoding changed");
            var decoded = EntityGraphCodec.Decode(encoded, World);
            Check(encoded.SequenceEqual(EntityGraphCodec.Encode(decoded)), "random roundtrip changed");
            Reject(Change(g, 299, n => n with { Attachment = new(299, EntityHolderSlot.Contents) }));
        }
    }
}
