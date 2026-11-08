# Authoritative dedicated server design

Status: architecture proposal + an isolated CI-built and unit-tested core; not a playable dedicated server.

## Hard boundary

The original multiplayer stays in lockstep mode with protocol 54. New code lives in Source/AuthoritativeCore, with a separate net8 project and protocol v1. It is NOT wired into the original network handlers, game bootstrap or save system. Activating it does not modify existing behavior.

Proposed production subsystems:

1. Unity/Verse SimulationHost: must own Current.Game, Find, maps, game object IDs, RNG, Scribe saves and the sole game simulation thread. The transport-neutral AuthoritativeSimulationHost is now implemented: it enqueues transport commands with bounded backpressure, drains a limited number on the owner thread, dispatches game-tick callbacks and requests a consistent snapshot. The actual IAuthoritativeGameDriver binding to Verse is NOT implemented.
2. SimulationDomainManager (initial implementation): a single-threaded 60-base-tick scheduler, per-domain tick/speed/pause, export/restore of clock metadata. Implemented in Source/AuthoritativeCore/SimulationDomainManager.cs; no Verse callback is presently connected.
3. CommandProcessor (initial implementation): validates authenticated connection identity, declared faction, owned domain, protocol, state revision and unique command ID. It enforces owning-thread execution and invokes an ICommandExecutor, to be implemented against a real Verse runtime. A failing executor must not partially mutate the game.
4. WorldStateReplicator (pending): server-only creation/update/deletion journal, stable network identifiers, snapshot cut at a sim-thread barrier, per-subscriber interest filtering, sequenced and acknowledged deltas.
5. ClientStateReplica (initial implementation): applies typed snapshots and consecutive deltas into an atomic read-only DTO store. This is not a RimWorld Thing presentation bridge.
6. Durable SaveCoordinator (pending): save all maps/world, clocks, authoritative net IDs, outstanding operations and command receipts consistently; temp file and recovery slot rotation.
7. AuthoritativeTransport (pending): authentication, bounded messages, session ownership, reliable sequence delivery, acks, resync requests and backpressure.

## Non-negotiable invariants

- The server is the only writer of gameplay state. Rendering, audio and interpolation may run client-side but cannot mutate authority.
- Never trust session IDs or faction IDs in network payloads. The network connection supplies the authenticated identity to the command processor.
- Commands are deduplicated BEFORE checking state revision, making delivery retries idempotent within the current in-memory receipt ledger.
- New map IDs and network IDs are allocated only by the server and survive restarts. RimWorld Thing IDs alone are not yet approved for this role.
- A client snapshot replaces replica state only after all records pass validation; deltas must follow exact revision order.
- Gameplay callbacks must be made on the simulation thread; the current scheduler enforces creator-thread affinity.
- No cross-domain transfer may commit without an atomic server-owned transaction and a time policy.

## Immediate limitations of implemented code

The production IAuthoritativeGameDriver/ICommandExecutor binding and ICommandAuthorization session provider do not exist yet. The test project supplies test implementations only. The command receipt ledger is in memory, bounded at 100,000 IDs by default, and not persistent; it fails closed when full. StateVersion is an injected value, not a complete entity revision vector. No command is executed through Verse and no network sockets are opened. The simplified ThingState (ID, domain, faction, X, Z) is only a transport example; it cannot reconstruct a pawn or a map.

The scheduler is deliberately NOT a substitute for RimWorld's AsyncTimeComp: it supplies when-to-tick decisions, not how to-tick RimWorld gameplay. ClientStateReplica stores DTOs and is not a proof that local gameplay ticks can be disabled without breaking UI.

## Implementation sequence

A. Verify a real RimWorld/Unity headless bootstrap and safe map tick on one thread (separate process/entrypoint, no reuse of net-only Server.cs as a game host).
B. Add a read-only client mode gated by an explicit authoritative connection handshake; guard all Verse ticking entrypoints and preserve visual updates.
C. Implement server-reconstructible snapshot and one pawn's move command using real game IDs, pawn jobs and a client presentation bridge.
D. Add deterministic entity-change journals and reconnect-on-snapshot; then test map lifecycle and multifaction.
E. Only after the single-pawn vertical slice is proven, expand to world events, independent time and cross-domain transaction policies.
