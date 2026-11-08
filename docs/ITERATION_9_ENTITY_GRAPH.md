# Iteration 9: bounded containment graph and atomic world view

## Scope

Built on iteration 8 (`c9de740dde57268b691ced6b76f8d736370e07d5`), preserving RimWorld 1.6, server-owned Verse simulation and presentation-only clients. No Amity code, binaries, assets or protocol have been imported. This is another Stage A foundation, not a playable multiplayer release.

The old `WorldBootstrapV2` schema and v1 localhost protocol are unchanged. A new independent graph describes spawned and held entities. A new in-memory `WorldViewCheckpoint` pairs the graph with a v2 map bootstrap. There is no network handler for this pair yet.

## Data model

`EntityGraphSnapshot` has its own format version, world UUID, network revision, game revision, map dimensions, entity nodes and explicit holder declarations. Nodes reuse the immutable scalar `EntityPresentation` record. Pawn data remains flags and current job Def, not full health, needs, skills, genes, equipment stats or a Scribe graph.

Each node has exactly one `EntityAttachment`:

- `(HolderEntityId = 0, Slot = None)` is a map root; its X/Z must be in bounds.
- A positive holder ID and named slot locate a held entity; X/Z must both be `-1`, not the stale Verse coordinates of a despawned Thing.

Slots are `Inventory`, `Equipment`, `Apparel`, `Carry` for pawns and `Contents` for non-pawn entity holders. Empty holders are explicit. A child points to a declared `(OwnerEntityId, Slot)`, not an independent duplicate list of child IDs. This avoids two conflicting sources of containment truth. Every node must eventually reach a map root in the same domain.

Containment does not imply faction ownership: a carried pawn may belong to another faction. Validation is not authorization. Accepted server snapshots may legitimately change holder/location/faction; only future validated server commands and transaction rules may cause gameplay changes. The client has no inventory-transfer command in this iteration.

Example (IDs are illustrative): pawn 1 owns inventory slot containing bag 6; bag 6 owns a Contents slot containing meal stack 7. Only pawn 1 is a map root. IDs remain the iteration-8 server registry IDs, not newly minted graph-local identifiers.

## Validation and bounds

`EntityGraphValidator` rejects invalid metadata, duplicate IDs/maps/holders, missing owners or slots, inconsistent node kinds, invalid Def/job strings, invalid stack/rotation/coordinates, multiple Carry children, cross-domain parent links, cycles and excessive depth. Parent chains are walked iteratively and memoized; no recursive traversal depends on untrusted graph depth. All returned collections are read-only canonical copies, sorted by map domain, entity ID and owner/slot respectively.

Limits: at most 16 maps of 512 by 512 cells, 100,000 nodes, 100,000 holder declarations, 4,096 children per holder (Carry: one), and 32 containment edges to a root. Names are at most 128 UTF-16 code units and 256 strict UTF-8 bytes. Each serialized graph is at most 8 MiB including checksum. These are initial fail-closed limits, not measured production capacity. Retired identity entries remain subject to iteration 8's separate lifetime registry limit.

`EntityGraphCodec` implements a separate bounded binary format. It checks byte counts before collection/string allocation, strict encoding, known enums/flags, checksum and world identity, then performs complete referential validation. Encoding checks its growing output budget while writing records. Decoding hashes and reads the same defensive input copy. A valid checksum cannot bypass structural checks. SHA-256 is corruption detection, not a server signature or anti-cheat mechanism.

### Binary graph v1 layout

Little-endian fields, no compression. `Guid` uses .NET `Guid.ToByteArray()` representation.

```text
"RWEG"; format:i32; world:16; network:i64; game:i64
mapCount:i32; maps[domain:i32, width:i32, height:i32]
nodeCount:i32; nodes[
  id:i64, domain:i32, faction:i32, def:utf8,
  x:i32, z:i32, rotation:i32, stack:i32, kind:u8,
  if Pawn: flags:u8, currentJob:nullableUtf8,
  holderId:i64, slot:u8
]
holderCount:i32; holders[ownerId:i64, slot:u8]
SHA256:32 (digest of all preceding bytes)
```

`utf8` uses a u16 byte length; zero is allowed only for nullable job names and denotes null. Pawn flag bits 0/1/2 mean Drafted/Downed/Dead; all other bits are rejected. Trailing bytes and unknown format versions are rejected. This encoding is not added to TCP v1 and must be explicitly negotiated by future transport.

## Atomic presentation install

`WorldViewCheckpointValidator` validates both copies, requires identical UUID and revisions, matches all map domains/dimensions, and checks that the graph's map roots exactly equal bootstrap entities, including pawn flags/job. Both encoded payloads must fit their respective budgets. Held nodes are not duplicated among the bootstrap roots.

`ClientWorldViewReplica` pins the expected world and local content manifest on its creating presentation thread. Invalid, foreign, incompatible or older candidates leave the previous view unchanged. Exact canonical retries of the same network revision succeed; a different payload under the same revision is rejected as `RevisionConflict`. The commit point occurs only after both parts and their serialization succeed. Reading exposes a defensive snapshot copy.

This is whole-checkpoint replacement, not delta application, a crash transaction or a network reassembly buffer. It may serialize/copy data multiple times and stores canonical bytes for retry comparison; do not call it at simulation tick frequency without profiling. Content matching inherits the existing manifest limitations (resolved Def identity hash, not complete semantic settings admission).

## Verse read adapter

`VerseWorldViewCapture.Capture(driver, worldTile, authoritativeManifest, networkVersion)` is a server-side API. It first captures the real v2 bootstrap using the existing driver, then traverses the same paused map and known holders using the same persisted identity component. Capture checks revision/world consistency and validates the complete pair before returning.

The collector reads pawn inventory, equipment, apparel and carry trackers via `IThingHolder`. For non-pawn Things exposing a direct `IThingHolder`, it represents direct contents under `Contents`. Nested Thing holders are visited as their own entities. It checks `ParentHolder`, duplicate runtime references, destroyed/spawned state and traversal budgets. It never issues a job, spawns a Thing, transfers inventory or ticks client simulation.

Additional advertised holders and component-only `ThingComp` holders without explicit slot semantics cause `NotSupportedException`; they are not silently omitted or flattened. Therefore generic modded containers are NOT supported by default, and even vanilla holder layouts must pass the runtime acceptance below. The adapter does not support caravans, unloaded/world-held entities or independent active multi-map time domains. It captures private server state and has no interest/fog/visitor filtering: do not deliver the full graph to an untrusted remote client.

Identity allocation is metadata mutation, not gameplay mutation. A failed capture can reserve IDs for objects visited before the failure; it does not publish a partial view or recycle those IDs. The allocator/registry limits and save-boundary caveats from iteration 8 still apply.

## Automated verification

A dedicated `Source/AuthoritativeEntityGraph.Tests` executable adds 22 cases. The existing CI workflow builds the original projects and runs the original core/transport suites plus the new suite. Cases cover all slots and nested contents, canonical ordering, duplicate/dangling references, locations/domains, slot/cardinality rules, cycles/depth, count/byte budgets, names/pawn fields, truncation/corruption, valid-checksum malformed input, defensive copies, pair consistency, atomic install, world/content/revision admission, retries, presentation transfers, thread affinity, and 20 seeded forests of 300 nodes each.

```sh
dotnet run --project Source/AuthoritativeEntityGraph.Tests/AuthoritativeEntityGraph.Tests.csproj -c Release
dotnet run --project Source/AuthoritativeCore.Tests/AuthoritativeCore.Tests.csproj -c Release
dotnet run --project Source/AuthoritativeTransport.Tests/AuthoritativeTransport.Tests.csproj -c Release
dotnet build Source/AuthoritativeRimWorldBridge/AuthoritativeRimWorldBridge.csproj -c Release
```

A green CI run means compiled reference-assembly integration and standalone tests. It does not prove runtime container enumeration or Scribe behavior. Consult the exact commit's Actions run for actual results; no local .NET SDK or licensed RimWorld runtime was available in the editing environment.

## Required RimWorld 1.6 runtime acceptance (not executed)

1. Use a backed-up disposable single-map colony, paused vanilla time and the existing trusted driver. Build the manifest from the real installation, not the unit-test fixture hashes. Call the capture API on the driver's Unity thread. Inspect root equality and map/world/revision metadata.
2. Equip and dress a pawn, put stacks in its inventory, have it carry an item or another pawn, and capture again with a new network revision. Inspect exactly one location per entity and empty holder declarations. Verify the actual tracker `GetDirectlyHeldThings` and `ParentHolder` behavior.
3. Exercise available direct container Things, corpse/minified/casket-like cases and nesting. These are acceptance candidates, NOT claimed runtime-supported examples. Additional holder/component types must fail explicitly rather than yield a deceptively complete graph.
4. Use ordinary server gameplay to move an item map -> inventory -> map and to destroy an item; capture new checkpoints. Check stable IDs and removal of old locations. Do not treat this test as a newly implemented player transfer command.
5. Save/reload the test game using iteration 8's checklist and compare graph IDs. Confirm the loader/Unity thread handoff and corruption handling before trusting persistence.
6. Feed a client replica mismatched map/graph revisions, a dangling holder and a cyclic graph. Verify the prior accepted view remains intact. Confirm no Verse map/Thing creation or client gameplay tick occurs.

## Remaining work

Full Pawn/Thing component state, additional holder adapters, delta streams, v2 session negotiation, chunked checkpoint delivery, authenticated/interest-filtered admission and read-only rendering are still incomplete. Durable whole-world saves, command replay after crashes, trade/ownership transactions and cross-time-domain travel remain later stages. No production safety, runtime performance or playable client is claimed.
