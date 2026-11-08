# Amity-inspired integration roadmap for the authoritative RimWorld server

**Planning document only.** Reviewed source: user-supplied Amity 0.3.33 archive (`scribl.amity`) for RimWorld 1.7, inspected through static DLL metadata, XML and assets. Amity's server implementation was **not** present. Findings about method/class names are architectural clues, not proof of its runtime behavior.

Our target remains **RimWorld 1.6, server-owned Verse simulation, read-only gameplay clients, eventual independent colony time domains**. We will independently implement useful concepts, **not import Amity code, DLLs, wire formats, assets or game rules**. The archive states *All rights reserved* and did not include its referenced `LICENSE.txt`; direct reuse would require a separate permission/license review.

## At a glance: planned features and implementation status

Legend: **foundation** means a narrow component exists and is CI-tested, but its real RimWorld integration is **not** complete. **Planned** means no implementation claim.

| Priority | Amity observation (from supplied client DLLs) | Our independent target | Status as of iteration 7 | Acceptance gate |
| --- | --- | --- | --- | --- |
| **P0** | `AmityMapSnapshot` (`CaptureTerrain/Things/Pawns`, `BuildMap`) plus lightweight `SnapshotPawn` | Full initial map bootstrap **separate from** high-frequency entity deltas; chunking, revision handling and safe snapshot resync | **Foundation:** `WorldBootstrapV2`, bounded DTO codec, read-only Verse source, v1 movement delta publisher. **Not networked v2 or rendered** | Real client reconstructs/map-displays a checkpoint and receives ordered changes **without locally ticking gameplay**; missing revisions trigger resync |
| **P0** | `AmityPawnDetailsSnapshot`, `AmityPawnBlobCodec`, `AmityThingSnapshot` and item references | Typed pawn + thing components: health, hediffs, genes, age, skills, traits, needs, jobs, equipment, inventory, containers, faction, create/delete and reference resolution | **Partial:** ID/position/Def, pawn flags and job Def only; no deep state | Validate/bound roundtrip of object graphs with explicit parent/holder links; unknown references cannot mutate client state |
| **P0** | ID remapping while restoring objects | Persistent server-owned entity IDs, world UUID, object relationship registry; IDs must not be taken blindly from Verse `thingIDNumber` | **Planned:** provisional IDs only | IDs survive save/reload and rejoin; no collision, dangling holder, duplicated pawn or unauthorized owner migration |
| **P0** | `AmitySaveSync`, `AmityIdentityGate`, `AmityAtomicFile` and `BaseVersion`/SHA-256/world ID | **Server-owned** Scribe checkpoint + journal; atomic write/replace, world identity, consistency revision, command receipts and crash recovery | **Planned:** only runtime clock metadata export / in-memory command receipts | Truncated/corrupt checkpoint fails closed; crash restart replays committed operations once and rejects foreign/stale worlds |
| **P1** | `AmityManifestClient.CaptureLocalMods/Check/BuildOrderWarnings` | Admission gate comparing game build, ordered loaded mods, selected assemblies, patches/settings and semantically resolved Def values | **Foundation:** full-folder SHA-256, active mod enumeration, Def **identity-only** hash and `ClientWorldBootstrapReplica` check; **not a security-complete join gate** | Mismatches reject join before world loading; all server-side hashes derive from actual active game state; tests exercise both matched and mismatched modded installs |
| **P1** | `Amity.Protocol.dll` typed packets and `AmityNetClient` reconnect/outbox/sequence handling | Versioned *own* authenticated protocol; chunked checkpoints with checksums; bounded queues, rate limits, ack, reconnect backoff, replay protection and production TLS | **Foundation:** v1 loopback TCP, command receipts, delta history and length bounds. No encrypted remote transport or v2 handshake | Loss/duplicate/reorder/slow-client tests; atomic checkpoint install and safe recovery; remote transport never uses cleartext credentials |
| **P1** | `GameComponent_AmityWorld.Reconcile`, `AmityRemoteSettlement/Caravan/Camp` | Server-owned remote settlements, factions, caravans and camps reflected on the shared world map as **read-only** client projections | **Planned** | Remote owners cannot mutate each other's worlds; joins/reconnects reconcile world objects without duplicates |
| **P1** | `AmityMapSession.Open/OpenPve/OpenLoot` and temporary map projections | Controlled viewing/visiting of another colony: server-created access/session, interest filtering and safe remote map presentation | **Planned** | Unauthorized visitor sees no hidden colony state; UI interactions are routed as validated server commands, not direct Verse mutations |
| **P2** | `AmityTradeSession` `Revision/ReadyA/B/EscrowedA/B/CommitAt/ExpiresAt`; `AmityTradeJournal` | Cross-domain trade coordinator: unique transaction ID, escrow/reservation, prepare/commit/abort, durable journal, retry-safe receipt, economic and time-order rules | **Planned** | Duplicated/delayed messages and crash midway cannot duplicate items, silver or pawns; paused/ahead domains follow documented reject policy |
| **P2** | Explicit `RaidQueued/RaidLanded/RaidResolved` event states | Server-scheduled raid/incursion/event lifecycle with durable IDs, single resolution and interest-aware replication | **Planned** | Each event transitions once, survives reconnect/replay and cannot spawn a second raid on retry |
| **P2** | Shared planet interactions, caravan travel and remote camps | Server-owned caravan/world travel, visits and transfers between independently timed colonies | **Planned** | All map/holder/ownership transfers atomic; no future-to-past state transfer; reject unless clocks and world transaction policy allow it |
| **P3** | Chat, presence, `Ping/Pong`, reconnect/status packets | Chat/presence/admin UI and diagnostics separate from simulation commands and critical state updates | **Planned** | Rate-limited bounded messages; chat cannot delay server ticks; no unauthenticated identity or owner spoofing |

## Ordered engineering sequence (dependencies matter)

**Stage A — complete a real server-to-client world view (P0 / P1).** Finish stable entity IDs, richer bounded `Pawn/Thing/Container` DTOs, authoritative checkpoint revisions, v2 negotiation and chunked transfer. Build a truly read-only client map renderer and incremental entity changes. Our current v2 schema and extracted map layers are **only the foundation**. Live Unity validation is deferred at the user's request, but must remain an acceptance gate.

**Stage B — consistency, saves and compatibility (P0 / P1).** Implement world UUID persisted alongside Scribe saves, atomic checkpoints, durable per-command receipts and recovery journal. Finish full semantic Def hashing (current `DefCatalogSha256` hashes **identities only**), active mod DLL/patch/settings checks and proper admission before world state is delivered. Keep the game/build/mod-content fingerprint versioned.

**Stage C — reliable production sessions (P1).** Own protocol negotiation, bounded chunk windows, ack/checksum of snapshot parts, backpressure, heartbeat and reconnect backoff; then authenticated encrypted remote transport. Our current loopback TCP remains test-only, bound to localhost.

**Stage D — shared world (P1).** Introduce `WorldObjectReplica`, `FactionOwnership` and independent remote colony/settlement/caravan/camp records. Add viewing/visiting states with server-side permissions. This is NOT a client-side transfer of authority and not the same as Amity's particular gameplay mode.

**Stage E — cross-domain gameplay (P2).** Add domain-aware caravan travel and transfer, explicit durable trade escrow journal and raid/world-event state machines. Keep transactions **disabled** until crash consistency, ownership and different-time-domain policies are proven. See `docs/TIME_DOMAINS.md`.

**Stage F — social and operational features (P3).** Add chat, presence, status, telemetry and moderation after core persistence and transport correctness.

## Explicit "do not copy" and architectural differences

- **No reuse of Amity DLLs, copyrighted classes or implementations, icons, binary `ProtocolConstants.Version=7` format, or its remote API.** It targets RimWorld 1.7/.NET 10/Harmony 3, while ours targets RimWorld 1.6/net48 with a separate core.
- Do **not** treat the lightweight Amity pawn location stream as a complete authoritative RimWorld state serializer.
- Do **not** let client-uploaded saves, pawn blobs or transaction declarations overwrite server state. The server is the only authority for world simulation, saves and item ownership.
- Do **not** rely on phantom PvE pawns or side-effecting local Verse maps to simulate game state. The server executes real jobs, damage, raids, inventory and time; clients only present the results.
- Do **not** claim Amity has a Verse-ticking authoritative server, secure trade commit, or crash-safe recovery: the supplied archive did not contain its server code.

## Current evidence and acceptance policy

CI can currently compile the standalone net8/netstandard2 core, net48 Verse bridge, local test mod and probe client, and run core/socket tests. **There has been no licensed RimWorld 1.6/Unity runtime test**, no fully populated deep pawn graph and no playable client. Any future implementation must be tracked as source-compiled, CI-tested, game-tested, or production-ready separately, not conflated.

This roadmap tracks **future original implementation of ideas suggested by Amity**; it is not a request to integrate third-party binaries.
