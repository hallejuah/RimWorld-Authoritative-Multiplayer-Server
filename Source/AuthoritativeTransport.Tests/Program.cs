using System.IO;
using Multiplayer.Authoritative;
using Multiplayer.Authoritative.Transport;

var tests = new (string Name, Action Test)[]
{
    ("TCP handshake and initial authoritative snapshot", HandshakeAndSnapshot),
    ("TCP move command, delta, replay and reconnect", MoveAndReconnect),
    ("TCP cannot spoof another session or faction", RejectSpoofing),
    ("TCP rejects wrong local credentials", RejectCredential),
    ("Bounded delta history requests a fresh snapshot", HistoryGap),
    ("Frame parser rejects oversized and truncated traffic", BadFrames),
    ("Unchanged state yields an empty poll", EmptyPoll)
};
var fails = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine("PASS: " + name); }
    catch (Exception e)
    {
        Console.Error.WriteLine("FAIL: " + name + ": " + e);
        fails++;
    }
}
Console.WriteLine($"{tests.Length - fails}/{tests.Length} transport tests passed");
return fails == 0 ? 0 : 1;

static T Wait<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
static void Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static PlayerCommand Move(string session, int faction, long state, int x) =>
    new(AuthoritativeProtocol.Version, Guid.NewGuid(), session, faction, 10, state,
        AuthoritativeCommandKind.MovePawn, 100, x, 7, 0);
static void Until(Func<bool> condition)
{
    for (var i = 0; i < 1500 && !condition(); i++) Thread.Sleep(2);
    Check(condition(), "Timed out waiting for network worker to queue command");
}

static TestRig NewRig(int historyLimit = 4) => new(historyLimit);

static void HandshakeAndSnapshot()
{
    using var rig = NewRig();
    using var client = Wait(LoopbackTransportClient.ConnectAsync(rig.Server.Port, "valid-test-token"));
    Check(client.Replica.Version == 0, "snapshot network version");
    Check(client.Replica.GameStateVersion == 7, "snapshot must expose game revision");
    Check(client.Replica.Things[100].X == 1, "snapshot pawn location");
}

static void MoveAndReconnect()
{
    using var rig = NewRig();
    using (var client = Wait(LoopbackTransportClient.ConnectAsync(rig.Server.Port, "valid-test-token")))
    {
        var command = Move("alice", 1, client.Replica.GameStateVersion, 11);
        var pending = client.SendCommandAsync(command);
        Until(() => rig.Host.QueuedCommandCount == 1);
        Check(rig.Driver.Applied == 0, "network worker performed game simulation");
        rig.Host.Pump(TimeSpan.Zero);
        rig.Feed.Publish(rig.Driver.CaptureSnapshot());
        Check(Wait(pending).Accepted && rig.Driver.Applied == 1, "game thread rejected command");
        Check(Wait(client.PollAsync()) == ReplicaApplyResult.Applied, "delta poll");
        Check(client.Replica.Version == 1 && client.Replica.GameStateVersion == 8 &&
            client.Replica.Things[100].X == 11, "replica did not receive movement");

        // Retry the same UUID after the game revision changed: no duplicate move.
        var retry = client.SendCommandAsync(command);
        Until(() => rig.Host.QueuedCommandCount == 1);
        rig.Host.Pump(TimeSpan.Zero);
        Check(Wait(retry).Accepted && rig.Driver.Applied == 1, "duplicate command was re-executed");
    }

    // Rejoining receives a new snapshot instead of replaying past commands.
    using var rejoined = Wait(LoopbackTransportClient.ConnectAsync(rig.Server.Port, "valid-test-token"));
    Check(rejoined.Replica.Version == 1 && rejoined.Replica.Things[100].X == 11,
        "reconnect snapshot did not restore position");
}

static void RejectSpoofing()
{
    using var rig = NewRig();
    using var client = Wait(LoopbackTransportClient.ConnectAsync(rig.Server.Port, "valid-test-token"));
    var foreignSession = client.SendCommandAsync(Move("bob", 1, 7, 90));
    Until(() => rig.Host.QueuedCommandCount == 1);
    rig.Host.Pump(TimeSpan.Zero);
    Check(!Wait(foreignSession).Accepted, "client forged session");

    var foreignFaction = client.SendCommandAsync(Move("alice", 2, 7, 90));
    Until(() => rig.Host.QueuedCommandCount == 1);
    rig.Host.Pump(TimeSpan.Zero);
    Check(!Wait(foreignFaction).Accepted, "client controlled foreign faction");
    Check(rig.Driver.Applied == 0, "unauthorized command changed game");
}

static void RejectCredential()
{
    using var rig = NewRig();
    Throws<InvalidOperationException>(() =>
    {
        using var unauthorized = Wait(LoopbackTransportClient.ConnectAsync(rig.Server.Port, "invalid"));
    });
}

static void HistoryGap()
{
    using var rig = NewRig(historyLimit: 1);
    using var old = Wait(LoopbackTransportClient.ConnectAsync(rig.Server.Port, "valid-test-token"));
    rig.Driver.MoveDirect(5);
    rig.Feed.Publish(rig.Driver.CaptureSnapshot());
    rig.Driver.MoveDirect(9);
    rig.Feed.Publish(rig.Driver.CaptureSnapshot());
    Check(rig.Feed.LatestSnapshot().Version == 2, "two revisions not recorded");
    Check(!rig.Feed.TryReadAfter(0, out _), "expired revisions were replayed");
    Check(Wait(old.PollAsync()) == ReplicaApplyResult.Applied, "resync snapshot not accepted");
    Check(old.Replica.Version == 2 && old.Replica.Things[100].X == 9, "resync state mismatch");
}

static void BadFrames()
{
    // 8 MiB + 2 announced, rejected before allocation of the payload.
    using var big = new MemoryStream(new byte[] { 0x00, 0x80, 0x00, 0x02 });
    Throws<InvalidDataException>(() => Wait(WireFrameCodec.ReadAsync(big)));
    using var partial = new MemoryStream(new byte[] { 0, 0 });
    Throws<EndOfStreamException>(() => Wait(WireFrameCodec.ReadAsync(partial)));
    using var outbound = new MemoryStream();
    Throws<InvalidDataException>(() => Wait(WireFrameCodec.WriteAsync(outbound,
        WireFrameKind.Command, new byte[AuthoritativeProtocol.MaxCommandBytes + 1])));
}

static void EmptyPoll()
{
    using var rig = NewRig();
    using var client = Wait(LoopbackTransportClient.ConnectAsync(rig.Server.Port, "valid-test-token"));
    Check(Wait(client.PollAsync()) == ReplicaApplyResult.Applied, "empty poll failed");
    Check(client.Replica.Version == 0, "unchanged poll advanced revision");
}

sealed class TestRig : IDisposable
{
    public FakeDriver Driver { get; } = new();
    public StatePublicationHub Feed { get; }
    public AuthoritativeSimulationHost Host { get; }
    public LoopbackTransportServer Server { get; }

    public TestRig(int historyLimit)
    {
        Feed = new StatePublicationHub(historyLimit);
        Host = new AuthoritativeSimulationHost(new TestPermissions(), Driver, () => Driver.GameVersion);
        Host.Domains.AddDomain(10, 1);
        Feed.Initialize(Driver.CaptureSnapshot());
        Server = new LoopbackTransportServer(new TestCredentials(), Host, Feed);
        Server.Start();
    }

    public void Dispose() => Server.Dispose();
}

sealed class TestCredentials : ILoopbackAuthenticator
{
    public bool TryAuthenticate(string credential, out string authenticatedSessionId)
    {
        authenticatedSessionId = credential == "valid-test-token" ? "alice" : "";
        return authenticatedSessionId.Length > 0;
    }
}

sealed class TestPermissions : ICommandAuthorization
{
    public bool TryGetFaction(string authenticatedSessionId, out int factionId)
    {
        factionId = authenticatedSessionId == "alice" ? 1 : 0;
        return factionId > 0;
    }

    public bool OwnsDomain(int factionId, int domainId) => factionId == 1 && domainId == 10;
}

sealed class FakeDriver : IAuthoritativeGameDriver
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private ThingState pawn = new(100, 10, 1, 1, 7);
    public int Applied { get; private set; }
    public long GameVersion { get; private set; } = 7;

    public bool TryExecute(PlayerCommand command, out string reason)
    {
        EnsureThread();
        if (command.Kind != AuthoritativeCommandKind.MovePawn ||
            command.TargetThingId != pawn.Id)
        {
            reason = "Unsupported test command";
            return false;
        }
        MoveDirect(command.X);
        Applied++;
        reason = "Moved in test driver";
        return true;
    }

    public void MoveDirect(int x)
    {
        EnsureThread();
        pawn = pawn with { X = x };
        GameVersion++;
    }

    public void ExecuteGameTick(int domainId, long nextTick) => EnsureThread();

    public WorldSnapshot CaptureSnapshot()
    {
        EnsureThread();
        return new WorldSnapshot(GameVersion, [pawn]);
    }

    private void EnsureThread()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Fake engine callback on network thread");
    }
}
