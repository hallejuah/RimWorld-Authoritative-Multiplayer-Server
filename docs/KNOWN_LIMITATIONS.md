# Known limitations and acceptance status

As of the second authoritative feature-branch iteration:

## Implemented source components

- A multi-targeted net8/netstandard2.0 AuthoritativeCore assembly with typed v1 protocol and size-limited JSON codec.
- A command admission gate checking authenticated session/faction/domain, expected revision and duplicate UUIDs.
- An atomic, version-ordered read-only client DTO replica.
- Single-threaded independent map-clock scheduling with supported pause/1x/2x/3x and exported clock metadata.
- A bounded cross-thread command queue and owner-thread AuthoritativeSimulationHost pump, with a separately compiled, opt-in single-map Verse bridge.
- A .NET 8 console test runner that exercises command processing, domain mapping, snapshot-to-delta updates, and client DTO replication; CI status is recorded in docs/BUILD_AND_TEST.md.

## Not implemented or not verified

- Not connected to the existing LiteNetLib network transport or authenticated session establishment. The host provides only the transport-neutral enqueue/pump boundary.
- A RimWorldSingleMapDriver with real TickManager.DoSingleTick and Pawn_JobTracker.TryTakeOrderedJob calls now compiles, but there is NO deployed headless Unity host, startup hook, runtime confirmation or playable server.
- An opt-in Unity tick guard exists for the server-side single-map experiment, but no reliable client gameplay-tick suppression, object presentation layer, or full client-server move order path exists.
- A minimal real-pawn position projection and full-scan DTO delta generator exist. No complete game snapshot serializer, map generation service, stable persisted entity IDs, health/needs, inventory or jobs replication exists.
- No persistence for command receipts, world snapshots, whole-world saves, or crash-safe recovery.
- No server-side global world simulation, faction creation protocol or atomic cross-domain interactions.
- No verified client reconnect, message acks, deltas resend buffer, compression or interest management.
- Unit tests and original solution tests executed successfully in GitHub Actions; no RimWorld/Unity integration tests executed. The interactive sandbox lacks dotnet and game files.
- No DLC compatibility tests, load tests, production deployment artifact, Linux/Windows game run or independent client UI test.

## Risks

1. Verse global/static state, Unity main-thread requirements and map context switching complicate server game execution.
2. Scribe XML world saves are not equivalent to a minimal typed network snapshot.
3. Client Unity UI may invoke game logic outside the normal TickManager tick patch, so merely skipping TickPatch is not sufficient.
4. Arbitrary cross-time transactions may cause duplication and causal paradoxes; these must remain blocked until a real coordinator exists.
5. The bounded in-memory deduplication ledger is not safe across restarts, and cannot be used as production anti-replay storage.
6. Version numbers are a minimal ordering mechanism, not a full per-entity causal history.
7. The current 8 MiB message cap is a bound, not full fragmentation, compression or resource-exhaustion protection.

## Next required acceptance gate

Obtain a licensed RimWorld 1.6 runtime, establish a game simulation bootstrap on a Unity thread, and prove ONE pawn moves from a genuine server-side command while a client displays the change without executing any authoritative local gameplay tick. Only then can the Stage 1 gameplay criterion be marked complete.


## Second-iteration safety notes

- The Verse bridge requires one loaded map and paused vanilla time. It maps map.uniqueID N to network DomainId N+1.
- It must never be considered a general-purpose independent map-time implementation: DoSingleTick advances the global RimWorld world.
- The opt-in Harmony update hook is not an authenticated server network handshake. Normal game/UI code can mutate state outside tick callbacks; this needs investigation before a client read-only mode can be claimed.
- The simplified snapshot contains pawn locations and faction IDs only; it is insufficient for client map creation, pawns' jobs, pathfinding, saves, or reconnect.
- The bridge's source was compiled against references, not tested inside a licensed RimWorld 1.6 game.
