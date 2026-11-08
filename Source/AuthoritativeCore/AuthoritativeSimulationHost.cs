using System.Collections.Concurrent;

namespace Multiplayer.Authoritative;

// A game driver must be bound to an actual RimWorld/Verse simulation.
// The core does not provide or emulate game logic.
public interface IAuthoritativeGameDriver : ICommandExecutor
{
    void ExecuteGameTick(int domainId, long nextTick);
    WorldSnapshot CaptureSnapshot();
}

// Transport threads only enqueue commands. A single owner thread performs all
// admission, game callbacks, clock updates and consistent snapshot captures.
public sealed class AuthoritativeSimulationHost
{
    private const int MaxCommandsPerPump = 64;
    private readonly IAuthoritativeGameDriver driver;
    private readonly AuthoritativeCommandProcessor processor;
    private readonly ConcurrentQueue<PendingCommand> incoming = new();
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly int queueLimit;
    private int queuedCommands;

    public SimulationDomainManager Domains { get; } = new();

    public AuthoritativeSimulationHost(
        ICommandAuthorization authorization,
        IAuthoritativeGameDriver driver,
        Func<long> currentStateVersion,
        int maximumQueuedCommands = 2048)
    {
        if (authorization is null) throw new ArgumentNullException(nameof(authorization));
        if (driver is null) throw new ArgumentNullException(nameof(driver));
        if (currentStateVersion is null) throw new ArgumentNullException(nameof(currentStateVersion));
        if (maximumQueuedCommands <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumQueuedCommands));

        this.driver = driver;
        queueLimit = maximumQueuedCommands;
        processor = new AuthoritativeCommandProcessor(authorization, driver, currentStateVersion);
    }

    public int QueuedCommandCount => Volatile.Read(ref queuedCommands);

    // Called by transport after authentication. This does NOT execute the command.
    public Task<CommandReceipt> EnqueueCommand(string authenticatedSessionId, PlayerCommand command)
    {
        if (authenticatedSessionId is null) throw new ArgumentNullException(nameof(authenticatedSessionId));
        if (command is null) throw new ArgumentNullException(nameof(command));

        if (Interlocked.Increment(ref queuedCommands) > queueLimit)
        {
            Interlocked.Decrement(ref queuedCommands);
            return Task.FromResult(new CommandReceipt(command.CommandId, false, "Command queue full"));
        }

        var completion = new TaskCompletionSource<CommandReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        incoming.Enqueue(new PendingCommand(authenticatedSessionId, command, completion));
        return completion.Task;
    }

    // Must be called by a real Unity/Verse host from the simulation thread.
    public void Pump(TimeSpan elapsed)
    {
        EnsureThread();
        for (var i = 0; i < MaxCommandsPerPump && incoming.TryDequeue(out var item); i++)
        {
            Interlocked.Decrement(ref queuedCommands);
            try
            {
                var receipt = processor.Process(item.SessionId, item.Command);
                item.Completion.TrySetResult(receipt);
            }
            catch (Exception error)
            {
                item.Completion.TrySetException(error);
                // Do not quietly continue after an uncertain game mutation.
                throw;
            }
        }

        Domains.Advance(elapsed, driver.ExecuteGameTick);
    }

    public WorldSnapshot CaptureSnapshot()
    {
        EnsureThread();
        return driver.CaptureSnapshot();
    }

    private void EnsureThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Host operations require the Unity simulation thread");
    }

    private sealed record PendingCommand(
        string SessionId,
        PlayerCommand Command,
        TaskCompletionSource<CommandReceipt> Completion);
}
