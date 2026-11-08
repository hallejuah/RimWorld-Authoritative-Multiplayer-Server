namespace Multiplayer.Authoritative;

// This wire format is independent of the existing lockstep protocol (54).
public static class AuthoritativeProtocol
{
    public const int Version = 1;
    public const int MaxCommandBytes = 16 * 1024;
    public const int MaxStateMessageBytes = 8 * 1024 * 1024;
}

public enum AuthoritativeCommandKind
{
    MovePawn = 1,
    SetDomainSpeed = 2
}

public sealed record PlayerCommand(
    int ProtocolVersion,
    Guid CommandId,
    string SessionId,
    int FactionId,
    int DomainId,
    long ExpectedStateVersion,
    AuthoritativeCommandKind Kind,
    long TargetThingId,
    int X,
    int Z,
    int RequestedSpeed);

public sealed record CommandReceipt(Guid CommandId, bool Accepted, string Reason);

public sealed record ThingState(long Id, int DomainId, int FactionId, int X, int Z);

public enum ChangeKind
{
    Create = 1,
    Update = 2,
    Delete = 3
}

public sealed record StateChange(ChangeKind Kind, long ThingId, ThingState? State);

// Version is the ordered NETWORK revision; GameStateVersion is the authoritative
// game revision that PlayerCommand.ExpectedStateVersion must reference.
public sealed record WorldSnapshot(long Version, IReadOnlyList<ThingState> Things)
{
    public long GameStateVersion { get; init; }
}

public sealed record WorldDelta(long BaseVersion, long Version, IReadOnlyList<StateChange> Changes)
{
    public long GameStateVersion { get; init; }
}
