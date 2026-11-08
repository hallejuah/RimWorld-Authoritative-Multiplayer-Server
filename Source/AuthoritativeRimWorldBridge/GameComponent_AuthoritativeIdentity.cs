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
    private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
    private ServerEntityRegistry? registry;
    private ServerEntityBindings<Thing>? bindings;
    private string? savedWorldId;
    private string? savedCheckpoint;
    private string? loadFault;

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
        EnsureThread();
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            EnsureRegistry();
            // Absence from a map is NOT deletion: inventory, caravans and
            // despawned Things keep their identities. Only actual destruction
            // of a bound server object retires it here.
            foreach (var thing in bindings!.CaptureBoundObjects())
                if (thing.Destroyed) bindings.Retire(thing);
            savedWorldId = registry!.WorldId.ToString("D");
            savedCheckpoint = Convert.ToBase64String(
                EntityIdentityCheckpointCodec.Encode(registry.CaptureCheckpoint()));
        }

        Scribe_Values.Look(ref savedWorldId, "authoritativeWorldId");
        Scribe_Values.Look(ref savedCheckpoint, "authoritativeEntityIdentities");

        if (Scribe.mode == LoadSaveMode.LoadingVars)
            RestoreMetadata();
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            // The validated registry owns the state now. Do not retain a large
            // duplicate Base64 payload for the whole session.
            savedCheckpoint = null;
            savedWorldId = null;
        }
    }

    private void RestoreMetadata()
    {
        registry = null;
        bindings = null;
        loadFault = null;
        // Legacy saves without BOTH fields adopt an identity on first server
        // use/save. Empty, half-present or corrupt fields must NOT regenerate IDs.
        if (savedWorldId is null && savedCheckpoint is null) return;
        try
        {
            if (savedWorldId is null || savedCheckpoint is null ||
                !Guid.TryParseExact(savedWorldId, "D", out var expectedWorld) || expectedWorld == Guid.Empty ||
                savedCheckpoint.Length > ((EntityIdentityFormat.MaxBytes + 2) / 3) * 4)
                throw new InvalidDataException("Missing or invalid authoritative identity metadata");
            var checkpoint = EntityIdentityCheckpointCodec.Decode(
                Convert.FromBase64String(savedCheckpoint), expectedWorld);
            var restored = ServerEntityRegistry.Restore(checkpoint, expectedWorld);
            registry = restored;
            bindings = new ServerEntityBindings<Thing>(restored);
        }
        catch (Exception e) when (e is InvalidDataException || e is FormatException || e is ArgumentException)
        {
            // Keep the game inspectable, but deny authoritative capture, commands
            // and saving. Never silently replace the corrupt registry with a new one.
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
        registry = new ServerEntityRegistry(Guid.NewGuid());
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
        EnsureThread();
        if (!ReferenceEquals(Current.Game, ownerGame))
            throw new InvalidOperationException("Identity component belongs to another game");
    }

    private void EnsureThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != ownerThread)
            throw new InvalidOperationException("Scribe identity access requires the Unity owner thread");
    }
}
