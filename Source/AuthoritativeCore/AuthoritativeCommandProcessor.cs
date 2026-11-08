namespace Multiplayer.Authoritative;

// The transport must authenticate the connection before calling Process.
// Never use the session ID in the payload as proof of identity.
public interface ICommandAuthorization
{
    bool TryGetFaction(string authenticatedSessionId, out int factionId);
    bool OwnsDomain(int factionId, int domainId);
}

// The real RimWorld implementation must execute on the Unity simulation thread.
// No in-memory game simulation is provided by this assembly.
public interface ICommandExecutor
{
    bool TryExecute(PlayerCommand command, out string reason);
}

public sealed class AuthoritativeCommandProcessor(
    ICommandAuthorization authorization,
    ICommandExecutor executor,
    Func<long> currentStateVersion,
    int maximumRecordedCommands = 100_000)
{
    private readonly Dictionary<(string SessionId, Guid Id), (PlayerCommand Command, CommandReceipt Receipt)> receipts = new();

    public CommandReceipt Process(string authenticatedSessionId, PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(authenticatedSessionId);
        ArgumentNullException.ThrowIfNull(command);

        if (authenticatedSessionId.Length == 0)
            return Reject(command, "Unauthenticated session");

        // Check known IDs BEFORE state version, so a retry never applies twice.
        var key = (authenticatedSessionId, command.CommandId);
        if (command.CommandId != Guid.Empty && receipts.TryGetValue(key, out var previous))
            return previous.Command == command
                ? previous.Receipt
                : Reject(command, "Command ID reused with different payload");

        if (command.ProtocolVersion != AuthoritativeProtocol.Version)
            return Reject(command, "Unsupported authoritative protocol");
        if (command.CommandId == Guid.Empty)
            return Reject(command, "Command ID must not be empty");
        if (command.SessionId != authenticatedSessionId)
            return Reject(command, "Session identity mismatch");
        if (command.FactionId <= 0 || command.DomainId <= 0)
            return Reject(command, "Invalid faction or domain");
        if (!authorization.TryGetFaction(authenticatedSessionId, out var factionId) ||
            factionId != command.FactionId ||
            !authorization.OwnsDomain(factionId, command.DomainId))
            return Reject(command, "No authority over requested faction and domain");
        if (command.ExpectedStateVersion != currentStateVersion())
            return Reject(command, "Stale state version");
        if (command.Kind == AuthoritativeCommandKind.MovePawn && command.TargetThingId <= 0)
            return Reject(command, "Invalid pawn ID");
        if (command.Kind == AuthoritativeCommandKind.SetDomainSpeed &&
            command.RequestedSpeed is < 0 or > 3)
            return Reject(command, "Unsupported speed");
        if (command.Kind is not (AuthoritativeCommandKind.MovePawn or AuthoritativeCommandKind.SetDomainSpeed))
            return Reject(command, "Unsupported command");

        // Fail closed rather than evicting IDs and permitting their replay.
        // The receipt ledger must become durable before production use.
        if (receipts.Count >= maximumRecordedCommands)
            return Reject(command, "Command ledger capacity exhausted");

        // An exception must propagate to the host's logged error boundary.
        bool applied = executor.TryExecute(command, out var reason);
        var receipt = new CommandReceipt(command.CommandId, applied,
            applied ? "Accepted" : string.IsNullOrWhiteSpace(reason) ? "Rejected by game" : reason);
        receipts.Add(key, (command, receipt));
        return receipt;
    }

    public int RecordedCommandCount => receipts.Count;

    private static CommandReceipt Reject(PlayerCommand command, string reason) =>
        new(command.CommandId, false, reason);
}
