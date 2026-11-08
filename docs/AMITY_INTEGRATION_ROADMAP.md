# Amity-inspired integration roadmap for the authoritative RimWorld server

**Roadmap and implementation tracker.** Initial reviewed source: user-supplied Amity 0.3.33 archive (`scribl.amity`) for RimWorld 1.7, inspected through static DLL metadata, XML and assets. Amity's server implementation was **not** present. Findings about method/class names are architectural clues, not proof of its runtime behavior.

Our target remains **RimWorld 1.6, server-owned Verse simulation, read-only gameplay clients, eventual independent colony time domains**. We independently implement useful concepts, **not import Amity code, DLLs, wire formats, assets or game rules**. The reference archive states *All rights reserved* and did not include its referenced `LICENSE.txt`; direct reuse would require a separate permission/license review.

## At a glance: planned features and implementation status

Legend: **foundation** means a narrow component exists with automated checks, but its real RimWorld integration is **not** complete. **Planned** means no implementation claim. Consult the relevant commit's CI run for executed results.

| Priority | Amity observation (from supplied client DLLs) | Our independent target | Status as of iteration 10 | Acceptance gate |
| --- | --- | --- | --- | --- |
| **P0** | `AmityMapSnapshot` (`CaptureTerrain/Things/Pawns`, `BuildMap`) plus lightweight `SnapshotPawn` | Full initial map bootstrap separate from frequent deltas; chunking, revisions and resync | **Foundation:** map v2 + bounded entity graph + atomic view replica; new chunked loopback TCP API transfers the pair with checksums and reconnect staging. **Not wired to the game probe UI; no renderer** | Real client displays a captured colony without locally ticking gameplay, follows ordered changes and resyncs gaps |
| **P0** | `AmityPawnDetailsSnapshot`, `AmityPawnBlobCodec`, `AmityThingSnapshot` and item references | Typed pawn/thing components including health, hediffs, genes, age, skills, traits, needs, jobs, inventory, equipment and containers | **Partial:** bounded ID/Def/position + pawn flags/job graph; explicit inventory/equipment/apparel/carry/contents links; narrow Verse capture. **No deep pawn state or generic modded-holder support** | Bounded object graphs with explicit holder links; unknown references cannot mutate client state |
| **P0** | ID remapping while restoring objects | Persistent server-owned entity IDs, world UUID and relationships, not direct `thingIDNumber` identity | **Foundation:** allocator, tombstones, checkpoint codec, runtime bindings, Scribe adapter and presentation holder graph. **Live save/reload validation missing** | IDs survive save/reload/rejoin without collisions, dangling holders or unauthorized ownership changes |
| **P0** | `AmitySaveSync`, `AmityIdentityGate`, `AmityAtomicFile`, `BaseVersion`/SHA-256/world ID | Server-owned Scribe checkpoint + durable journal, receipts and crash recovery | **Metadata foundation only:** UUID and identity registry embedded in Scribe. **No atomic full-world save, durable receipts or recovery journal**; transport checkpoints are presentation data, not durable saves | Corrupt saves fail closed; committed operations recover exactly once; stale/foreign worlds rejected |
| **P1** | `AmityManifestClient.CaptureLocalMods/Check/BuildOrderWarnings` | Admission for game build, ordered mods, assemblies, patches/settings and semantic Def values | **Foundation:** active mod hashes and Def identity hash; chunked hello/offer checks a canonical ordered manifest digest before data/staging. **Not authenticated or semantically complete admission** | Mismatch rejected before world loading using actual active content, including modded test installations |
| **P1** | Typed packets and reconnect/outbox/sequence handling | Own versioned authenticated transport, chunk windows, checksums, backpressure, replay protection and TLS | **Foundation:** unchanged v1 probe plus separate chunked transfer v1; bounded frames, one-block pull window, final ACK, missing-block resume and finite deadlines. **Loopback only, no credentials/TLS or automatic retry policy in the new path** | Loss/duplicate/reorder/slow-peer checks, atomic install and secure authenticated remote sessions |
| **P1** | Remote settlement/caravan/camp reconciliation | Server-owned remote world objects and factions with read-only projections | **Planned** | Reconnect reconciles without duplicates; remote owners cannot mutate another colony |
| **P1** | Map sessions and temporary visiting projections | Controlled viewing/visiting with server permissions and interest filtering | **Planned** | Unauthorized visitors receive no hidden state; interactions use validated server commands |
| **P2** | Trade revision/readiness/escrow/commit/expiry and journal | Durable cross-domain prepare/commit/abort transactions and economic/time-order rules | **Planned** | Retries and crashes cannot duplicate items, silver or pawns; paused/ahead domain policies enforced |
| **P2** | Explicit raid lifecycle states | Server-scheduled event IDs, durable transitions and interest-aware replication | **Planned** | One event resolves once and cannot spawn again on retry/reconnect |
| **P2** | Shared planet travel, visits and camps | Server-owned caravan travel and transfers across time domains | **Planned** | Ownership/holder/map transfers atomic; no future-to-past state movement |
| **P3** | Chat, presence, ping/pong and status | Social/admin UI, diagnostics and moderation separate from simulation | **Planned** | Bounded/rate-limited messages cannot delay ticks or spoof identity |

## Ordered engineering sequence

**Stage A — a real server-to-client world view.** Iterations 8/9/10 provide persistent identity metadata, bounded containment, atomic map/graph replica install and a separately tested chunked loopback TCP path. Next connect trusted real-game capture to a local client presentation/renderer without local gameplay ticks; then ordered incremental updates. Finish actual Scribe and holder runtime acceptance, deep Pawn/Thing components and extra holder adapters. There is still **no playable client or live-game network validation**. Live Unity validation remains an acceptance gate.

**Stage B — consistency, saves and compatibility.** Complete runtime validation, atomic whole-world saves, durable command receipts, world/epoch rollback policy and recovery journal. Presentation checksums and in-memory resume do not provide durability. Finish semantic Def/settings/assembly/patch admission; current Def hashing covers identities only. Keep fingerprints explicitly versioned.

**Stage C — production sessions.** The new transport API verifies fixed format versions, checks bounded chunks and resumes an identical prepared snapshot. Remaining work includes integration/session lifecycle, authenticated world/epoch negotiation, server-wide limits/cache expiry, retry backoff/heartbeat, larger windows after measurement, and encrypted authenticated remote transport. Existing v1 and new chunked APIs remain local test paths. The new path has no credential authentication; never port-forward it.

**Stage D — shared world.** Add world object/faction ownership records for remote colonies, caravans and camps, then view/visit sessions with server permissions and interest filtering. Neither client possession of a snapshot nor containment transfers gameplay authority.

**Stage E — cross-domain gameplay.** Add travel/transfer coordinators, durable trade escrow and event state machines only after crash consistency, ownership and different-time policies are proven. Transactions remain disabled; see `docs/TIME_DOMAINS.md`.

**Stage F — social and operations.** Add chat, presence, telemetry and moderation after core persistence and transport correctness.

## Explicit architectural differences and exclusions

- No reuse of reference DLLs, implementation, assets, wire format or remote API. The inspected reference targets a different game/runtime stack; ours remains RimWorld 1.6/net48 with an isolated core.
- Lightweight pawn positions and flags are not a full authoritative state serializer. Rich pawn state and Thing components remain work.
- Client-uploaded saves/blobs/transactions cannot overwrite server state. The server alone owns simulation, saves and gameplay ownership.
- No phantom/local side-effecting maps to simulate client gameplay. The server executes actual Verse jobs, damage, raids and time; clients only present server results.
- No claim that the reference has a Verse-ticking authoritative server, secure trade commit or crash recovery: its server source was not supplied.
- No claim that chunk integrity or a matching content digest authenticates a peer. Full snapshots may contain hidden/private state and require filtering before any remote delivery.

## Iteration evidence and acceptance policy

`docs/ITERATION_8_ENTITY_IDENTITY.md`: allocator, Scribe metadata, 13 identity tests, migration limits and outstanding save/reload acceptance.

`docs/ITERATION_9_ENTITY_GRAPH.md`: bounded containment format, atomic view replica, 22 graph tests, narrow Verse capture and outstanding holder acceptance.

`docs/ITERATION_10_CHUNKED_CHECKPOINTS.md`: independent transport framing, prepare/assemble/transfer APIs, limits, resume/cleanup contracts, 32 new transfer tests and a future integration sequence. Implementation/test commit `ad69e8cf5e71513f23f57f15988afcae0188609b` passed run 42503982290: 47 core + 22 graph + 7 legacy transport + 32 new transfer = 108 cases. New socket tests use synthetic DTOs over real loopback TCP, not a live game.

CI compiles core/transport targets, the net48 bridge, test mod and probe client and runs focused suites. **No licensed RimWorld/Unity runtime test, full deep pawn serializer, read-only game renderer or production session is claimed.** Distinguish source-compiled, CI-tested, game-tested and production-ready. Inspect the exact commit's Actions run.
