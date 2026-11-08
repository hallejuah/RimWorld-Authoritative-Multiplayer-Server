using System;
using System.IO;
using System.Threading;
using Multiplayer.Authoritative;
using Verse;

namespace Multiplayer.Authoritative.RimWorldBridge;

// Stored INSIDE the server's ordinary Scribe save, not in a sidecar that could
// silently become detached. This is identity metadata, not crash-safe world saving.
public sealed class GameComponent_AuthoritativeIdentity : GameComponent
{
    private readonly Game ownerGame;
    // Game construction/loading may occur before the simulation owner exists.
    private int? ownerThread;
    private EntityIdentityCheckpoint? pendingCheckpoint;
    private ServerEntityRegistry? registry;
    private ServerEntityBindings<Thing>? bindings;
    private string? savedWorldId;
    private string? savedCheckpoint;
    private string? loadFault;
    private bool identityRequired;

    public GameComponent_AuthoritativeIdentity(Game game) =>
        ownerGame = game ?? throw new ArgumentNullException(nameof(game));

    public static GameComponent_AuthoritativeIdentity ForCurrentGame()
    {
        var game = Current.Game ?? throw new InvalidOperationException("No server game is loaded");
        return game.GetComponent<GameComponent_AuthoritativeIdentity>()
            ?? throw new InvalidOperationException("Authoritative identity component is not installed");
    }

    public Guid WorldId
    {
        get { EnsureCurrentGame(); EnsureRegistry(); return registry!.WorldId; }
    }

    public long GetOrAssignId(Thing thing)
    {
        EnsureCurrentGame();
        EnsureRegistry();
        return bindings!.GetOrAssignId(SourceKey(thing), thing);
    }

    public bool TryGetId(Thing thing, out long entityId)
    {
        EnsureCurrentGame();
        EnsureRegistry();
        return bindings!.TryGetId(SourceKey(thing), thing, out entityId);
    }

    public override void ExposeData()
    {
        // Write the adoption marker BEFORE identity capture can fail. If the
        // enclosing saver catches a component exception and still writes a save,
        // missing fields must not be mistaken for a clean legacy world on reload.
        if (Scribe.mode == LoadSaveMode.Saving) identityRequired = true;
        Scribe_Values.Look(ref identityRequired, "authoritativeIdentityRequired", false);

        if (Scribe.mode == LoadSaveMode.Saving)
        {
            if (loadFault is not null)
                throw new InvalidOperationException("Authoritative identity restore failed: " + loadFault);
            EntityIdentityCheckpoint checkpoint;
            if (registry is not null)
            {
                EnsureThread();
                // Despawn/inventory is NOT deletion. Only genuinely destroyed
                // bound server objects retire, and only on the owning thread.
                foreach (var thing in bindings!.CaptureBoundObjects())
                    if (thing.Destroyed) bindings.Retire(thing);
                checkpoint = registry.CaptureCheckpoint();
            }
            else
            {
                // Saving before the first host session must not bind the future
                // runtime registry to a Scribe/loading worker thread.
                checkpoint = pendingCheckpoint ?? new ServerEntityRegistry(Guid.NewGuid()).CaptureCheckpoint();
                pendingCheckpoint = checkpoint;
            }
            savedWorldId = checkpoint.WorldId.ToString("D");
            savedCheckpoint = Convert.ToBase64String(EntityIdentityCheckpointCodec.Encode(checkpoint));
        }

        Scribe_Values.Look(ref savedWorldId, "authoritativeWorldId");
        Scribe_Values.Look(ref savedCheckpoint, "authoritativeEntityIdentities");

        if (Scribe.mode == LoadSaveMode.LoadingVars)
            RestoreMetadata();
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            // The validated checkpoint owns the state now. Do not retain a large
            // duplicate Base64 payload for the whole session.
            savedCheckpoint = null;
            savedWorldId = null;
        }
    }

    private void RestoreMetadata()
    {
        registry = null;
        bindings = null;
        pendingCheckpoint = null;
        ownerThread = null;
        loadFault = null;
        // Only unmarked legacy saves without BOTH fields may adopt an identity.
        // Marked, empty, half-present or corrupt data must NOT regenerate IDs.
        if (!identityRequired && savedWorldId is null && savedCheckpoint is null) return;
        try
        {
            if (savedWorldId is null || savedCheckpoint is null ||
                !Guid.TryParseExact(savedWorldId, "D", out var expectedWorld) || expectedWorld == Guid.Empty ||
                savedCheckpoint.Length > ((EntityIdentityFormat.MaxBytes + 2) / 3) * 4)
                throw new InvalidDataException("Missing or invalid authoritative identity metadata");
            // Decode on the loading thread into an immutable checkpoint only.
            // The live, thread-affine registry is constructed on first host use.
            pendingCheckpoint = EntityIdentityCheckpointCodec.Decode(
                Convert.FromBase64String(savedCheckpoint), expectedWorld);
        }
        catch (Exception e) when (e is InvalidDataException || e is FormatException || e is ArgumentException)
        {
            // Keep the game inspectable, but deny authoritative capture and
            // commands. Identity serialization will throw on save, with the
            // required marker already written. Never silently regenerate IDs.
            loadFault = e.Message;
            Log.Error("[Authoritative] Entity identity metadata is invalid; server access is disabled: " + loadFault);
        }
    }

    private void EnsureRegistry()
    {
        EnsureThread();
        if (loadFault is not null)
            throw new InvalidOperationException("Authoritative identity restore failed: " + loadFault);
        if (registry is not null) return;
        registry = pendingCheckpoint is null
            ? new ServerEntityRegistry(Guid.NewGuid())
            : ServerEntityRegistry.Restore(pendingCheckpoint, pendingCheckpoint.WorldId);
        pendingCheckpoint = null;
        bindings = new ServerEntityBindings<Thing>(registry);
    }

    private static string SourceKey(Thing thing)
    {
        if (thing is null || thing.Destroyed || thing.thingIDNumber <= 0 || thing.def is null)
            throw new InvalidOperationException("Thing cannot supply a live server save-reference");
        // This only locates a server object across Scribe reloads. The numeric
        // thingIDNumber is never exposed as the network entity identity.
        return thing.GetUniqueLoadID();
    }

    private void EnsureCurrentGame()
    {
        if (!ReferenceEquals(Current.Game, ownerGame) || Current.ProgramState != ProgramState.Playing)
            throw new InvalidOperationException("Identity component requires its loaded server game");
        // Trusted game-thread adapters establish ownership, not the constructor
        // or an asynchronous save loader. Later calls must stay on that thread.
        if (ownerThread is null) ownerThread = Thread.CurrentThread.ManagedThreadId;
        EnsureThread();
    }

    private void EnsureThread()
    {
        if (ownerThread is null || Thread.CurrentThread.ManagedThreadId != ownerThread.Value)
            throw new InvalidOperationException("Scribe identity access requires the Unity owner thread");
    }
}
