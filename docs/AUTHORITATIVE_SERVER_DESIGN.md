# Authoritative dedicated server design

Status: CI-built and unit-tested core plus a source-compiled RimWorld 1.6 Verse integration bridge; NOT a playable dedicated server.

## Hard boundary

The original multiplayer stays in lockstep mode with protocol 54. New code lives in separately compiled AuthoritativeCore, AuthoritativeTransport and AuthoritativeRimWorldBridge projects. Protocol v1 does NOT use the original network handlers or automatic game bootstrap. The optional Unity host and local-only TCP bridge must be explicitly activated and do not modify lockstep by default.

Proposed production subsystems:

1. Unity/Verse SimulationHost: must own Current.Game, Find, maps, game object IDs, RNG, Scribe saves and the sole game simulation thread. The transport-neutral AuthoritativeSimulationHost is now implemented: it enqueues transport commands with bounded backpressure, drains a limited number on the owner thread, dispatches game-tick callbacks and requests a consistent snapshot. An opt-in RimWorldSingleMapDriver is now compiled against RimWorld 1.6 reference assemblies in Source/AuthoritativeRimWorldBridge. It invokes actual TickManager.DoSingleTick and TryTakeOrderedJob in a loaded paused single-map Unity game, but has NEVER been executed with licensed game files in this environment.
2. SimulationDomainManager (initial implementation): a single-threaded 60-base-tick scheduler, per-domain tick/speed/pause, export/restore of clock metadata. Implemented in Source/AuthoritativeCore/SimulationDomainManager.cs; a provisional single-map Verse callback is implemented but has no proven runtime startup.
3. CommandProcessor (initial implementation): validates authenticated connection identity, declared faction, owned domain, protocol, state revision and unique command ID. It enforces owning-thread execution and invokes an ICommandExecutor, implemented by the single-map Verse bridge for the MovePawn command only. A failing executor must not partially mutate the game.
4. WorldStateReplicator + StatePublicationHub (initial implementation): compare DTO snapshots, emit ordered changes and maintain bounded versioned history under a lock so network workers only read immutable copies. No dirty tracking, interest filtering, persistence or full map serialization.
5. ClientStateReplica (initial implementation): applies typed snapshots and consecutive deltas into an atomic read-only DTO store. This is not a RimWorld Thing presentation bridge.
6. Durable SaveCoordinator (pending): save all maps/world, clocks, authoritative net IDs, outstanding operations and command receipts consistently; temp file and recovery slot rotation.
7. AuthoritativeTransport (experimental loopback-only): bounded TCP frames, caller-provided local credential validation, network-session identity, initial snapshot, delta polling/resync and command receipts. It is not remotely secure: no TLS, no persistent identities or anti-replay ledger, no production deployment.

## Non-negotiable invariants

- The server is the only writer of gameplay state. Rendering, audio and interpolation may run client-side but cannot mutate authority.
- Never trust session IDs or faction IDs in network payloads. The network connection supplies the authenticated identity to the command processor.
- Commands are deduplicated BEFORE checking state revision, making delivery retries idempotent within the current in-memory receipt ledger.
- New map IDs and network IDs are allocated only by the server and survive restarts. RimWorld Thing IDs alone are not yet approved for this role.
- A client snapshot replaces replica state only after all records pass validation; deltas must follow exact revision order.
- Gameplay callbacks must be made on the simulation thread; the current scheduler enforces creator-thread affinity.
- No cross-domain transfer may commit without an atomic server-owned transaction and a time policy.

## Immediate limitations of implemented code

The compile-checked RimWorldSingleMapDriver binds a single loaded map to IAuthoritativeGameDriver, but an authenticated production transport/session provider and live Unity bootstrap do not exist yet. The test project supplies test implementations only. The command receipt ledger is in memory, bounded at 100,000 IDs by default, and not persistent; it fails closed when full. StateVersion is an injected value, not a complete entity revision vector. The bridge contains real Verse job and tick calls, but these have not run in the game and no network sockets are opened. The simplified ThingState (ID, domain, faction, X, Z) is only a transport example; it cannot reconstruct a pawn or a map.

The scheduler is deliberately NOT a substitute for RimWorld's AsyncTimeComp: it supplies when-to-tick decisions, not how to-tick RimWorld gameplay. ClientStateReplica stores DTOs and is not a proof that local gameplay ticks can be disabled without breaking UI.

## Implementation sequence

A. Verify a real RimWorld/Unity headless bootstrap and safe map tick on one thread (separate process/entrypoint, no reuse of net-only Server.cs as a game host).
B. Add a read-only client mode gated by an explicit authoritative connection handshake; guard all Verse ticking entrypoints and preserve visual updates.
C. Implement server-reconstructible snapshot and one pawn's move command using real game IDs, pawn jobs and a client presentation bridge.
D. Add deterministic entity-change journals and reconnect-on-snapshot; then test map lifecycle and multifaction.
E. Only after the single-pawn vertical slice is proven, expand to world events, independent time and cross-domain transaction policies.


## Integration experiment status

Source/AuthoritativeRimWorldBridge builds separately and is not installed by the original Multiplayer mod. SingleMapUnityTickLoop.Start is an explicit opt-in activation; it patches the vanilla TickManagerUpdate and pumps its host from Root_Play.Update. No invocation or authenticated network handshake is wired up. It assumes the vanilla game is paused, exactly one map is loaded, and the caller owns a Unity main-thread context. This preliminary control needs real in-game tests and a full audit of all other gameplay mutation paths. See docs/GAME_INTEGRATION_PROBE.md.


## Third iteration integration boundary

The optional LoadedSingleMapHost.StartLocalTransport opens a loopback-only TCP endpoint after SingleMapUnityTickLoop.Start. Its server workers enqueue commands into AuthoritativeSimulationHost; Unity's Root_Play.Update calls Pump and publishes a fresh snapshot/delta into StatePublicationHub. The only client currently provided is a DTO-only test/reference client. On stopping the Unity host, the local listener closes and pending command receipts are rejected.

This does NOT make the vanilla multiplayer UI a read-only authoritative client and does NOT solve headless Unity startup, full map state, arbitrary modded jobs, real save/reload or independent world/map timelines.


## Optional game-facing probe wrapper (fourth iteration)

Source/AuthoritativeProbeMod is now a standalone net48 Verse.Mod using mod-settings buttons for explicit activation and shutdown; it refuses to run alongside the original Multiplayer assembly. A cryptographically random one-run credential is shown only by copying from the settings screen, and the transport binds only to localhost. Source/AuthoritativeProbeClient provides a separate net8 read-only terminal client. scripts/package-authoritative-probe.sh can stage an isolated test mod with an explicit assembly allowlist. This implementation has passed source compilation but **has never been loaded or tested inside licensed RimWorld**. Details: docs/EXPERIMENTAL_PROBE.md.


## Fifth iteration — v2 bootstrap boundary, independently developed

A new `WorldBootstrapV2` data model describes packed map terrain, roof and fog, minimal pawn/thing presentation and a version-sensitive content manifest that can block incompatible clients before world install. Its codec enforces size and shape checks, and client admission is fail-closed and atomic. It is deliberately isolated from the v1 movement stream to avoid silently breaking existing protocol consumers.

This is an original implementation derived from interoperability requirements, not copied from Amity or another mod. The server has no trustworthy Verse-based v2 map extractor or mod/Def fingerprint collector yet, and it has not been runtime-tested in a licensed game. See docs/WORLD_BOOTSTRAP_V2.md.
