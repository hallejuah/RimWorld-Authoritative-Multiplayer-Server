using System.Buffers.Binary;
using System.Security.Cryptography;
using Multiplayer.Authoritative;

internal static class EntityIdentityTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("Entity IDs are server allocated and lookups never allocate", Allocation),
        ("Entity IDs and allocator survive checkpoint reload", Persistence),
        ("Retired entity IDs and source references cannot be reused", Retirement),
        ("Entity identity keys, capacity and overflow are bounded", Bounds),
        ("Identity restore rejects foreign, duplicate and invalid records", InvalidCheckpoint),
        ("Runtime bindings reject aliased and mutated server references", BindingSafety),
        ("Runtime objects rebind after reload without changing IDs", BindingRestoration),
        ("Entity identity state stays on its simulation thread", ThreadAffinity),
        ("Identity checkpoints reject truncation and corruption", CodecCorruption),
        ("Identity codec validates structure even with a valid checksum", CodecStructure),
        ("Identity checkpoints are canonical defensive copies", DefensiveCopies),
        ("Randomized identity retirement and reload preserve invariants", RandomizedRecovery)
    ];

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static ServerEntityRegistry NewRegistry(int capacity = EntityIdentityFormat.MaxEntities) =>
        new(Guid.NewGuid(), capacity);

    private static void Allocation()
    {
        var r = NewRegistry();
        Check(!r.TryGetId("Thing_9000", out var missing) && missing == 0 && r.Count == 0, "lookup allocated");
        var first = r.GetOrCreateId("Thing_9000");
        Check(first == 1 && first != 9000, "raw game ID became network identity");
        Check(r.GetOrCreateId("Thing_9000") == first && r.Count == 1, "unstable repeated ID");
        Check(r.GetOrCreateId("thing_9000") == 2, "source keys must be ordinal and case sensitive");
        Check(r.TryGetSourceKey(first, out var key) && key == "Thing_9000", "reverse lookup failed");
        Check(!r.TryGetSourceKey(-1, out _) && !r.TryGetSourceKey(99, out _), "unknown identity resolved");
    }

    private static void Persistence()
    {
        var r = NewRegistry();
        var pawn = r.GetOrCreateId("Pawn_1357");
        var item = r.GetOrCreateId("Steel_9876");
        var bytes = EntityIdentityCheckpointCodec.Encode(r.CaptureCheckpoint());
        var restored = ServerEntityRegistry.Restore(EntityIdentityCheckpointCodec.Decode(bytes, r.WorldId), r.WorldId);
        Check(restored.WorldId == r.WorldId && restored.GetOrCreateId("Steel_9876") == item &&
              restored.GetOrCreateId("Pawn_1357") == pawn, "load order changed IDs");
        Check(restored.GetOrCreateId("NewThing_10000") == 3, "allocator high-water mark lost");
    }

    private static void Retirement()
    {
        var r = NewRegistry();
        var old = r.GetOrCreateId("Pawn_old");
        Check(r.Retire(old) && !r.Retire(old) && !r.Retire(999), "retirement must be idempotent");
        Check(!r.TryGetId("Pawn_old", out _) && !r.TryGetSourceKey(old, out _), "retired ID resolved");
        Throws<InvalidOperationException>(() => r.GetOrCreateId("Pawn_old"));
        var restored = ServerEntityRegistry.Restore(r.CaptureCheckpoint(), r.WorldId);
        Throws<InvalidOperationException>(() => restored.GetOrCreateId("Pawn_old"));
        Check(restored.GetOrCreateId("Pawn_new") == old + 1 && restored.Count == 2, "retired ID reused");
    }

    private static void Bounds()
    {
        var r = NewRegistry(2);
        foreach (var key in new[] { "", " ", " padded", "line\nbreak", "a\0b", new string('x', 257),
                     new string('\u0430', 129), "\ud800" })
            Throws<ArgumentException>(() => r.GetOrCreateId(key));
        Check(r.Count == 0 && r.NextEntityId == 1, "rejected keys mutated allocator");
        r.GetOrCreateId(new string('\u0430', 128)); // Exactly 256 UTF-8 bytes.
        r.GetOrCreateId("second");
        r.Retire(1);
        Throws<InvalidOperationException>(() => r.GetOrCreateId("overflow"));
        Check(r.GetOrCreateId("second") == 2, "capacity blocked an existing ID");
        var exhausted = ServerEntityRegistry.Restore(new(EntityIdentityFormat.Version, r.WorldId,
            long.MaxValue, [new(long.MaxValue - 1, "last", false)]), r.WorldId);
        Check(exhausted.GetOrCreateId("last") == long.MaxValue - 1, "last ID lost");
        Throws<InvalidOperationException>(() => exhausted.GetOrCreateId("wrap"));
        Throws<ArgumentException>(() => new ServerEntityRegistry(Guid.Empty));
        Throws<ArgumentOutOfRangeException>(() => NewRegistry(0));
    }

    private static void InvalidCheckpoint()
    {
        var r = NewRegistry();
        r.GetOrCreateId("a");
        r.GetOrCreateId("b");
        var c = r.CaptureCheckpoint();
        var a = c.Entries[0];
        var b = c.Entries[1];
        var invalid = new[]
        {
            c with { FormatVersion = 99 }, c with { WorldId = Guid.Empty },
            c with { NextEntityId = 0 }, c with { NextEntityId = 2 },
            c with { Entries = null! }, c with { Entries = [a, a] },
            c with { Entries = [a, b with { SourceKey = "a" }] },
            c with { Entries = [a with { EntityId = -1 }] },
            c with { Entries = [a with { SourceKey = "\n" }] },
            c with { Entries = [null!] }
        };
        foreach (var bad in invalid)
            Throws<InvalidDataException>(() => ServerEntityRegistry.Restore(bad, r.WorldId));
        Throws<InvalidDataException>(() => ServerEntityRegistry.Restore(c, Guid.NewGuid()));
        Throws<InvalidDataException>(() => ServerEntityRegistry.Restore(c, r.WorldId, 1));
        Check(r.Count == 2 && r.NextEntityId == 3 && r.GetOrCreateId("a") == 1, "failed restore mutated live state");
    }

    private static void BindingSafety()
    {
        var r = NewRegistry();
        var b = new ServerEntityBindings<object>(r);
        var first = new object();
        var second = new object();
        var id = b.GetOrAssignId("a", first);
        Check(b.GetOrAssignId("a", first) == id, "same object rebound");
        Throws<InvalidOperationException>(() => b.GetOrAssignId("a", second));
        Throws<InvalidOperationException>(() => b.GetOrAssignId("changed", first));
        Throws<InvalidOperationException>(() => b.TryGetId("a", second, out _));
        Check(!b.TryGetId("unknown", second, out _) && r.Count == 1, "command lookup minted identity");
        Check(b.TryResolve(id, out var found) && ReferenceEquals(found, first), "binding corrupted");
        Check(b.Retire(first) && !b.Retire(first) && !b.TryResolve(id, out _), "retired object resolved");
        Throws<InvalidOperationException>(() => b.GetOrAssignId("a", first));
        Throws<InvalidOperationException>(() => b.GetOrAssignId("new", first));
    }

    private static void BindingRestoration()
    {
        var r = NewRegistry();
        var before = new ServerEntityBindings<AlwaysEqual>(r);
        var a = new AlwaysEqual();
        var b = new AlwaysEqual();
        var idA = before.GetOrAssignId("a", a);
        var idB = before.GetOrAssignId("b", b);
        Check(idA != idB, "overridden object equality aliased bindings");
        var restored = ServerEntityRegistry.Restore(r.CaptureCheckpoint(), r.WorldId);
        var after = new ServerEntityBindings<AlwaysEqual>(restored);
        Check(!after.TryResolve(idA, out _), "runtime reference leaked into checkpoint");
        var loadedA = new AlwaysEqual();
        Check(after.TryGetId("a", loadedA, out var loadedId) && loadedId == idA, "save reference did not rebind");
        Check(after.TryResolve(idA, out var found) && ReferenceEquals(found, loadedA), "resolved old runtime object");
    }

    private static void ThreadAffinity()
    {
        var r = NewRegistry();
        var b = new ServerEntityBindings<object>(r);
        var value = new object();
        var id = b.GetOrAssignId("a", value);
        Action[] calls =
        [
            () => r.GetOrCreateId("b"), () => r.TryGetId("a", out _),
            () => r.TryGetSourceKey(id, out _), () => r.Retire(id),
            () => r.CaptureCheckpoint(), () => { _ = r.Count; },
            () => b.TryResolve(id, out _), () => b.GetOrAssignId("a", value),
            () => b.CaptureBoundObjects()
        ];
        foreach (var call in calls)
        {
            Exception? error = null;
            var thread = new Thread(() => { try { call(); } catch (Exception e) { error = e; } });
            thread.Start();
            thread.Join();
            Check(error is InvalidOperationException, "cross-thread identity access succeeded");
        }
        Check(r.Count == 1 && r.TryGetId("a", out _), "worker partially mutated registry");
    }

    private static void CodecCorruption()
    {
        var r = NewRegistry();
        r.GetOrCreateId("pawn");
        var bytes = EntityIdentityCheckpointCodec.Encode(r.CaptureCheckpoint());
        for (var length = 0; length < bytes.Length; length++)
            Throws<InvalidDataException>(() => EntityIdentityCheckpointCodec.Decode(bytes[..length], r.WorldId));
        for (var index = 0; index < bytes.Length; index++)
        {
            var changed = (byte[])bytes.Clone();
            changed[index] ^= 1;
            Throws<InvalidDataException>(() => EntityIdentityCheckpointCodec.Decode(changed, r.WorldId));
        }
        Throws<InvalidDataException>(() => EntityIdentityCheckpointCodec.Decode(bytes, Guid.NewGuid()));
        Throws<InvalidDataException>(() => EntityIdentityCheckpointCodec.Decode(
            new byte[EntityIdentityFormat.MaxBytes + 1], r.WorldId));
        var empty = NewRegistry();
        Check(EntityIdentityCheckpointCodec.Decode(EntityIdentityCheckpointCodec.Encode(empty.CaptureCheckpoint()),
            empty.WorldId).Entries.Count == 0, "valid empty registry rejected");
    }

    private static void CodecStructure()
    {
        var r = NewRegistry();
        r.GetOrCreateId("a");
        r.GetOrCreateId("b");
        var bytes = EntityIdentityCheckpointCodec.Encode(r.CaptureCheckpoint());
        void Reject(Action<byte[]> mutate)
        {
            var changed = (byte[])bytes.Clone();
            mutate(changed);
            Rehash(changed);
            Throws<InvalidDataException>(() => EntityIdentityCheckpointCodec.Decode(changed, r.WorldId));
        }
        Reject(b => b[0] = 0); // magic
        Reject(b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 99));
        Reject(b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), 1)); // reused allocator
        Reject(b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(32), -1));
        Reject(b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(32), EntityIdentityFormat.MaxEntities + 1));
        Reject(b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(32), 1)); // trailing record
        Reject(b => b[44] = 2); // invalid retired flag
        Reject(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(45), 0));
        Reject(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(45), 257));
        Reject(b => b[47] = 0xff); // invalid UTF-8
        Reject(b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(48), 1)); // duplicate ID
        Reject(b => b[59] = (byte)'a'); // duplicate source key
        var truncatedBody = new byte[bytes.Length - 1];
        Array.Copy(bytes, truncatedBody, truncatedBody.Length - 32);
        Rehash(truncatedBody);
        Throws<InvalidDataException>(() => EntityIdentityCheckpointCodec.Decode(truncatedBody, r.WorldId));
    }

    private static void Rehash(byte[] bytes) =>
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes, bytes.Length - 32);

    private static void DefensiveCopies()
    {
        var r = NewRegistry();
        r.GetOrCreateId("a");
        r.GetOrCreateId("b");
        var c = r.CaptureCheckpoint();
        var input = c.Entries.ToArray();
        var restored = ServerEntityRegistry.Restore(c with { Entries = input }, r.WorldId);
        input[0] = input[0] with { SourceKey = "changed" };
        Check(restored.GetOrCreateId("a") == 1 && restored.Count == 2, "restore retained mutable input array");
        Throws<NotSupportedException>(() => ((IList<EntityIdentityEntry>)c.Entries)[0] = input[0]);
        var canonical = EntityIdentityCheckpointCodec.Encode(c);
        var reversed = EntityIdentityCheckpointCodec.Encode(c with { Entries = c.Entries.Reverse().ToArray() });
        Check(canonical.SequenceEqual(reversed), "checkpoint encoding depends on enumeration order");
        r.Retire(1);
        Check(!c.Entries[0].Retired, "captured checkpoint changed with registry");
    }

    private static void RandomizedRecovery()
    {
        for (var seed = 0; seed < 3; seed++)
        {
            var rng = new Random(seed);
            var r = NewRegistry();
            var expected = new Dictionary<string, (long Id, bool Retired)>();
            for (var step = 0; step < 1200; step++)
            {
                var key = "entity_" + rng.Next(160);
                if (!expected.TryGetValue(key, out var model))
                {
                    var id = r.GetOrCreateId(key);
                    Check(id == expected.Count + 1, "allocator reused an ID");
                    expected.Add(key, (id, false));
                }
                else if (model.Retired)
                    Throws<InvalidOperationException>(() => r.GetOrCreateId(key));
                else if (rng.Next(4) == 0)
                {
                    Check(r.Retire(model.Id), "active retirement failed");
                    expected[key] = (model.Id, true);
                }
                else Check(r.GetOrCreateId(key) == model.Id, "existing ID changed");
                if (step % 31 == 0)
                    r = ServerEntityRegistry.Restore(EntityIdentityCheckpointCodec.Decode(
                        EntityIdentityCheckpointCodec.Encode(r.CaptureCheckpoint()), r.WorldId), r.WorldId);
                foreach (var pair in expected)
                {
                    var active = r.TryGetId(pair.Key, out var actual);
                    Check(active == !pair.Value.Retired && (!active || actual == pair.Value.Id), "model mismatch after reload");
                }
            }
        }
    }

    private sealed class AlwaysEqual
    {
        public override bool Equals(object? obj) => obj is AlwaysEqual;
        public override int GetHashCode() => 1;
    }
}
