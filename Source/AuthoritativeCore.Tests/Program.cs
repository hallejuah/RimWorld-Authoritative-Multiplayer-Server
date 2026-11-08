using Multiplayer.Authoritative;

var cases = new (string Name, Action Run)[]
{
    ("Command replay executes only once", CommandIdempotence),
    ("Faction ownership is enforced", FactionIsolation),
    ("Transport session cannot be spoofed", SessionSpoofing),
    ("Stale commands are rejected", StaleCommand),
    ("Recently observed move revisions may be accepted", RecentMove),
    ("Unsupported speed is rejected", InvalidSpeed),
    ("Snapshot and deltas are applied", SnapshotAndDeltas),
    ("Gaps and invalid updates do not partially mutate", ReplicaIntegrity),
    ("Typed protocol roundtrips and limits", ProtocolRoundtrip),
    ("Domains tick independently and restore clocks", DomainClocks),
    ("Domain callbacks stay on one thread", ThreadAffinity),
    ("Commands run only on their owning simulation thread", CommandThreadAffinity),
    ("Host queues commands and ticks on simulation thread", HostPipeline),
    ("Host rejects command queue overload", HostBackpressure),
    ("Host drains pending receipts when stopped", HostStopDrains),
    ("Zero-based RimWorld map IDs are mapped to positive domains", MapDomainMapping),
    ("Replicator publishes ordered pawn changes", ReplicatorDeltas),
    ("Replicator rejects duplicate or migrated identities", ReplicatorSafety),
    ("A failed game tick does not advance its clock", FailedTick),
    ("V2 bootstrap map layers and entities roundtrip", WorldBootstrapTests.Roundtrip),
    ("V2 bootstrap rejects invalid terrain and fog", WorldBootstrapTests.InvalidMap),
    ("V2 bootstrap rejects dangling entities and IDs", WorldBootstrapTests.InvalidEntity),
    ("V2 mod manifest rejects reordered and changed mods", WorldBootstrapTests.ManifestMismatch),
    ("V2 client admission is atomic across invalid updates", WorldBootstrapTests.AtomicAdmission),
    ("V2 snapshots are deep copied and digest is canonical", WorldBootstrapTests.DefensiveCopies),
    ("V2 codec rejects malformed and oversized payloads", WorldBootstrapTests.MalformedPayload)
};

var failures = 0;
foreach (var test in cases)
{
    try
    {
        test.Run();
        Console.WriteLine("PASS: " + test.Name);
    }
    catch (Exception e)
    {
        failures++;
        Console.Error.WriteLine("FAIL: " + test.Name + ": " + e);
    }
}
Console.WriteLine($"{cases.Length - failures}/{cases.Length} tests passed");
return failures == 0 ? 0 : 1;

static PlayerCommand Move(string session = "alice", int faction = 1, int domain = 10,
    long expectedVersion = 7) =>
    new(AuthoritativeProtocol.Version, Guid.NewGuid(), session, faction, domain,
        expectedVersion, AuthoritativeCommandKind.MovePawn, 100, 4, 5, 0);

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
}

static void MustThrow<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Expected exception: " + typeof(T).Name);
}

static AuthoritativeCommandProcessor Processor(TestExecutor executor, TestAuthorization? auth = null,
    long version = 7) => new(auth ?? new TestAuthorization(), executor, () => version);

static void CommandIdempotence()
{
    var executor = new TestExecutor();
    var processor = Processor(executor);
    var command = Move();
    Check(processor.Process("alice", command).Accepted, "first command rejected");
    Check(processor.Process("alice", command).Accepted, "duplicate receipt changed");
    Check(executor.Applied == 1 && processor.RecordedCommandCount == 1, "duplicate execution");
    Check(!processor.Process("alice", command with { X = 9 }).Accepted, "reused ID accepted");
    Check(executor.Applied == 1, "changed payload executed");
}

static void FactionIsolation()
{
    var executor = new TestExecutor();
    var processor = Processor(executor);
    Check(!processor.Process("alice", Move(faction: 2)).Accepted, "foreign faction controlled");
    Check(!processor.Process("alice", Move(domain: 20)).Accepted, "foreign domain controlled");
    Check(executor.Applied == 0, "unauthorized command reached executor");
}

static void SessionSpoofing()
{
    var executor = new TestExecutor();
    var processor = Processor(executor);
    Check(!processor.Process("bob", Move(session: "alice")).Accepted, "spoofed session");
    Check(executor.Applied == 0, "spoofed command executed");
}

static void StaleCommand()
{
    var executor = new TestExecutor();
    Check(!Processor(executor, version: 500).Process("alice", Move(expectedVersion: 1)).Accepted, "stale command");
    Check(executor.Applied == 0, "stale command executed");
}

static void RecentMove()
{
    var executor = new TestExecutor();
    Check(Processor(executor, version: 100).Process("alice",
        Move(expectedVersion: 95)).Accepted, "valid recent command rejected");
    Check(!Processor(executor, version: 100).Process("alice",
        Move(expectedVersion: 101)).Accepted, "future game revision accepted");
    Check(executor.Applied == 1, "revision check executed invalid command");
}

static void InvalidSpeed()
{
    var executor = new TestExecutor();
    var command = Move() with { Kind = AuthoritativeCommandKind.SetDomainSpeed, RequestedSpeed = 5 };
    Check(!Processor(executor).Process("alice", command).Accepted, "invalid speed accepted");
    Check(executor.Applied == 0, "invalid speed executed");
}

static void SnapshotAndDeltas()
{
    var replica = new ClientStateReplica();
    var pawn = new ThingState(100, 10, 1, 0, 0);
    Check(replica.InstallSnapshot(new WorldSnapshot(12, [pawn])) == ReplicaApplyResult.Applied, "snapshot");
    var pawnMoved = pawn with { X = 6 };
    var item = new ThingState(101, 10, 1, 7, 8);
    Check(replica.ApplyDelta(new WorldDelta(12, 13,
        [new StateChange(ChangeKind.Update, 100, pawnMoved),
         new StateChange(ChangeKind.Create, 101, item)])) == ReplicaApplyResult.Applied, "delta");
    Check(replica.Things is not Dictionary<long, ThingState>, "mutable state dictionary exposed");
    Check(replica.Version == 13 && replica.Things[100].X == 6 && replica.Things.ContainsKey(101),
        "replica content");
    Check(replica.ApplyDelta(new WorldDelta(13, 14,
        [new StateChange(ChangeKind.Delete, 101, null)])) == ReplicaApplyResult.Applied, "delete");
    Check(!replica.Things.ContainsKey(101), "deleted object survives");
}

static void ReplicaIntegrity()
{
    var replica = new ClientStateReplica();
    var pawn = new ThingState(100, 10, 1, 1, 2);
    Check(replica.InstallSnapshot(new WorldSnapshot(4, [pawn])) == ReplicaApplyResult.Applied, "initial");
    Check(replica.ApplyDelta(new WorldDelta(6, 7,
        [new StateChange(ChangeKind.Delete, 100, null)])) == ReplicaApplyResult.NeedsSnapshot, "gap ignored");
    Check(replica.ApplyDelta(new WorldDelta(4, 5,
        [new StateChange(ChangeKind.Update, 100, pawn with { X = 9 }),
         new StateChange(ChangeKind.Delete, 999, null)])) == ReplicaApplyResult.NeedsSnapshot,
        "invalid delta accepted");
    Check(replica.Version == 4 && replica.Things[100].X == 1, "partial mutation");
    Check(replica.InstallSnapshot(new WorldSnapshot(6, [pawn, pawn])) == ReplicaApplyResult.NeedsSnapshot,
        "duplicate ID accepted");
    Check(replica.Version == 4, "invalid snapshot committed");
}

static void ProtocolRoundtrip()
{
    var cmd = Move();
    Check(ProtocolCodec.DecodeCommand(ProtocolCodec.EncodeCommand(cmd)) == cmd, "command codec");
    var snapshot = new WorldSnapshot(1, [new ThingState(4, 10, 1, 3, 9)]);
    Check(ProtocolCodec.DecodeSnapshot(ProtocolCodec.EncodeSnapshot(snapshot)).Things[0].Id == 4,
        "snapshot codec");
    var delta = new WorldDelta(1, 2, [new StateChange(ChangeKind.Delete, 4, null)]);
    Check(ProtocolCodec.DecodeDelta(ProtocolCodec.EncodeDelta(delta)).Changes[0].ThingId == 4,
        "delta codec");
    MustThrow<ArgumentException>(() => ProtocolCodec.DecodeCommand(new byte[AuthoritativeProtocol.MaxCommandBytes + 1]));
    MustThrow<System.Text.Json.JsonException>(() => ProtocolCodec.DecodeCommand("invalid"u8.ToArray()));
}

static void DomainClocks()
{
    var manager = new SimulationDomainManager();
    manager.AddDomain(10, 1);
    manager.AddDomain(20, 2);
    manager.SetSpeed(10, 3);
    manager.SetSpeed(20, 1);
    var calls = new Dictionary<int, int>();
    manager.Advance(TimeSpan.FromSeconds(1), (id, tick) =>
    {
        calls[id] = calls.GetValueOrDefault(id) + 1;
    });
    Check(manager.GetDomain(10).Tick == 180 && manager.GetDomain(20).Tick == 60, "wrong speeds");
    manager.SetSpeed(20, 0);
    manager.Advance(TimeSpan.FromSeconds(1), (_, _) => { });
    Check(manager.GetDomain(10).Tick == 360 && manager.GetDomain(20).Tick == 60, "pause leaked");
    var copy = new SimulationDomainManager();
    copy.RestoreClocks(manager.ExportClocks());
    Check(copy.GetDomain(10).Speed == 3 && copy.GetDomain(20).Speed == 0 &&
          copy.GetDomain(10).Tick == 360 && copy.GetDomain(20).Tick == 60, "restore");
    MustThrow<InvalidOperationException>(() => copy.AddDomain(10, 1));
}

static void ThreadAffinity()
{
    var manager = new SimulationDomainManager();
    manager.AddDomain(10, 1);
    manager.SetSpeed(10, 1);
    MustThrow<InvalidOperationException>(() =>
        Task.Run(() => manager.Advance(TimeSpan.FromSeconds(1), (_, _) => { }))
            .GetAwaiter().GetResult());
}

static void CommandThreadAffinity()
{
    var executor = new TestExecutor();
    var processor = Processor(executor);
    MustThrow<InvalidOperationException>(() =>
        Task.Run(() => processor.Process("alice", Move())).GetAwaiter().GetResult());
    Check(executor.Applied == 0, "network thread executed gameplay");
}

static void HostPipeline()
{
    var driver = new TestGameDriver();
    var host = new AuthoritativeSimulationHost(new TestAuthorization(), driver, () => 7);
    host.Domains.AddDomain(10, 1);
    host.Domains.SetSpeed(10, 1);

    // Simulate arrival from a transport worker, not the game thread.
    Task<CommandReceipt>? receiptTask = null;
    Task.Run(() => { receiptTask = host.EnqueueCommand("alice", Move()); }).GetAwaiter().GetResult();
    var pending = receiptTask ?? throw new Exception("network worker did not enqueue");
    Check(!pending.IsCompleted && host.QueuedCommandCount == 1, "command executed on worker");
    host.Pump(TimeSpan.FromSeconds(1));
    Check(pending.GetAwaiter().GetResult().Accepted, "command rejected");
    Check(driver.Applied == 1 && driver.Ticks == 60 && host.QueuedCommandCount == 0,
        "incorrect host dispatch");
    Check(host.CaptureSnapshot().Version == 7, "snapshot driver not called");
    MustThrow<InvalidOperationException>(() =>
        Task.Run(() => host.CaptureSnapshot()).GetAwaiter().GetResult());
}

static void ReplicatorDeltas()
{
    var source = new ThingState(100, 1, 5, 2, 3);
    var journal = new WorldStateReplicator();
    var baseline = journal.Start(new WorldSnapshot(10, [source]));
    var client = new ClientStateReplica();
    Check(baseline.Version == 0 && client.InstallSnapshot(baseline) == ReplicaApplyResult.Applied,
        "replication baseline");

    var moved = source with { X = 8, Z = 9 };
    var created = new ThingState(101, 1, 5, 6, 7);
    var change = journal.CaptureChanges(new WorldSnapshot(14, [created, moved]))
        ?? throw new Exception("Expected movement delta");
    Check(change.Version == 1 && change.BaseVersion == 0 &&
        change.Changes.Count == 2, "bad ordered delta");
    Check(client.ApplyDelta(ProtocolCodec.DecodeDelta(ProtocolCodec.EncodeDelta(change))) ==
        ReplicaApplyResult.Applied, "wire delta not applied");
    Check(client.Things[100].X == 8 && client.Things.ContainsKey(101), "replica mismatch");

    Check(journal.CaptureChanges(new WorldSnapshot(15, [created, moved])) is null, "spurious delta");
    Check(journal.Version == 1, "empty delta advanced revision");
    var removed = journal.CaptureChanges(new WorldSnapshot(16, [moved]))
        ?? throw new Exception("Expected removal delta");
    Check(removed.Version == 2, "deletion missing");
    Check(client.ApplyDelta(removed) == ReplicaApplyResult.Applied &&
        !client.Things.ContainsKey(101), "removed object persisted");
}

static void ReplicatorSafety()
{
    var journal = new WorldStateReplicator();
    var a = new ThingState(100, 1, 5, 2, 3);
    journal.Start(new WorldSnapshot(1, [a]));
    MustThrow<ArgumentException>(() =>
        journal.CaptureChanges(new WorldSnapshot(2, [a, a])));
    MustThrow<InvalidOperationException>(() =>
        journal.CaptureChanges(new WorldSnapshot(2, [a with { DomainId = 2 }])));
    MustThrow<InvalidOperationException>(() =>
        journal.CaptureChanges(new WorldSnapshot(0, [a])));
    Check(journal.Version == 0, "invalid state changed published version");
    Check(journal.CaptureChanges(new WorldSnapshot(3, [a])) is null, "invalid state was committed");
}

static void MapDomainMapping()
{
    Check(MapDomainIds.FromMapUniqueId(0) == 1, "Map 0 must be valid");
    Check(MapDomainIds.FromMapUniqueId(1) == 2, "Map 1 mapping");
    MustThrow<ArgumentOutOfRangeException>(() => MapDomainIds.FromMapUniqueId(-1));
    MustThrow<ArgumentOutOfRangeException>(() => MapDomainIds.FromMapUniqueId(int.MaxValue));
}

static void HostStopDrains()
{
    var host = new AuthoritativeSimulationHost(new TestAuthorization(), new TestGameDriver(), () => 7);
    var first = host.EnqueueCommand("alice", Move());
    var second = host.EnqueueCommand("alice", Move());
    Check(host.RejectPendingCommands("Stopped") == 2, "not all pending requests drained");
    Check(!first.GetAwaiter().GetResult().Accepted &&
        !second.GetAwaiter().GetResult().Accepted &&
        host.QueuedCommandCount == 0, "shutdown leaked requests");
}

static void HostBackpressure()
{
    var host = new AuthoritativeSimulationHost(new TestAuthorization(), new TestGameDriver(), () => 7,
        maximumQueuedCommands: 1);
    var first = host.EnqueueCommand("alice", Move());
    var overflow = host.EnqueueCommand("alice", Move());
    Check(!overflow.GetAwaiter().GetResult().Accepted, "queue overload accepted");
    Check(host.QueuedCommandCount == 1 && !first.IsCompleted, "queue mutated");
    host.Pump(TimeSpan.Zero);
    Check(first.GetAwaiter().GetResult().Accepted && host.QueuedCommandCount == 0,
        "pending command not drained");
}

static void FailedTick()
{
    var manager = new SimulationDomainManager();
    manager.AddDomain(10, 1);
    manager.SetSpeed(10, 1);
    MustThrow<InvalidOperationException>(() =>
        manager.Advance(TimeSpan.FromSeconds(1), (_, _) => throw new InvalidOperationException("test")));
    Check(manager.GetDomain(10).Tick == 0, "failed tick committed");
}

sealed class TestAuthorization : ICommandAuthorization
{
    public bool TryGetFaction(string session, out int faction)
    {
        faction = session == "alice" ? 1 : session == "bob" ? 2 : 0;
        return faction != 0;
    }

    public bool OwnsDomain(int faction, int domain) =>
        faction == 1 && domain == 10 || faction == 2 && domain == 20;
}

sealed class TestExecutor : ICommandExecutor
{
    public int Applied { get; private set; }

    public bool TryExecute(PlayerCommand command, out string reason)
    {
        Applied++;
        reason = "";
        return true;
    }
}

sealed class TestGameDriver : IAuthoritativeGameDriver
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    public int Applied { get; private set; }
    public int Ticks { get; private set; }

    public bool TryExecute(PlayerCommand command, out string reason)
    {
        AssertOwner();
        Applied++;
        reason = "";
        return true;
    }

    public void ExecuteGameTick(int domainId, long nextTick)
    {
        AssertOwner();
        Ticks++;
    }

    public WorldSnapshot CaptureSnapshot()
    {
        AssertOwner();
        return new WorldSnapshot(7, [new ThingState(100, 10, 1, 4, 5)]);
    }

    private void AssertOwner()
    {
        if (Environment.CurrentManagedThreadId != owner)
            throw new InvalidOperationException("Test game ran outside simulation thread");
    }
}
