# WorldBootstrap v2 — independent server-owned presentation bootstrap

**Status:** v2 schema, validation, bounded JSON codec, map capture builder and Verse-backed read-only map adapter are source-implemented and CI-compiled. The adapter has **NOT been executed in a real RimWorld game**; the trusted mod/Def manifest provider is not implemented and v2 is **NOT sent by the current v1 local TCP probe**.

## Motivation

The original v1 pawn-position snapshot intentionally cannot create a client's RimWorld map. An inspection of the separately supplied Amity mod highlighted useful requirements — terrain and roof/fog layers, pawn presentation records, stable IDs and mod/version compatibility gates — but no Amity source code, assets or binary dependencies are included or copied. Amity's license was not shown to permit copying its implementation.

The independent v2 schema prepares a *visual* initial-state representation. It is NOT a RimWorld Scribe save and must never be taken as proof that a client cannot simulate game rules.

## Format

`WorldBootstrapV2` has FormatVersion = 2, WorldId (nonzero UUID), NetworkVersion, GameStateVersion, ContentManifest, Maps and Entities. Both version numbers are nonnegative and independent.

`MapLayerSnapshot` has:
- positive DomainId, nonnegative WorldTile, Width/Height between 1 and 512;
- TerrainDefs (1..256 names) and exactly Width × Height bytes of 0-based terrain palette indexes;
- RoofDefs (0..255 names) and exactly Width × Height roof bytes (0 = no roof, 1..N = RoofDefs[index - 1]);
- packed FogBits with exactly ceil(Width × Height / 8) bytes, cell offset X + Z × Width and least-significant bit first;
- zero unused high bits in the last fog byte.

`EntityPresentation` has positive stable network ID, existing map DomainId, FactionId >= 0 (neutral entities permitted), DefName, 0-based in-bounds position X/Z, Rotation 0..3, positive StackCount and Kind Thing/Pawn. Pawn requires StackCount=1 and a typed `PawnPresentation` containing Drafted, Downed, Dead and optional job DefName. **No health graph, needs, equipment, inventory, genetics, vegetation growth, ThingComps, jobs state machine, map incidents, save-state ownership, world calendar or multiplayer UI state yet.**

## Content manifest admission

`ContentManifest` includes:
- RimWorldVersion matching the 1.6 family;
- loaded `ModFingerprint` entries in actual loading order (PackageId, Version, SHA-256 of mod content);
- DefCatalogSha256 for the loaded, resolved Def catalog.

The prototype accepts only equal game versions, mod order, mod versions, mod hashes and DefCatalogSha256; duplicates and malformed hashes are rejected. Package IDs and hex digests are normalized to lowercase during validation.

**Critical:** A separately compiled Verse manifest-input collector and complete mod-folder byte hashing now exist (see docs/MOD_CONTENT_FINGERPRINTS.md), but they have not been run in-game. The current Def digest covers only identities (type, name, shortHash), NOT all resolved semantic fields. Full compatibility and game/runtime security are not proven; never substitute client-declared or dummy hashes.

## Validation, determinism, integrity

`WorldBootstrapValidator.ValidateAndCopy` checks the entire graph: map dimensions, palette references, packed fog, unique map domains and entity IDs, bounds and domain references, pawn payload consistency, manifest uniqueness and size constraints. Only after all checks pass does it return a **deep defensive copy** of byte arrays and collections with maps/entities sorted by stable ID.

`WorldBootstrapCodec` handles typed `System.Text.Json` serialization only, max 8 MiB per encoded message, max depth 24 and no unknown JSON properties. The `ComputeSha256` digest is deterministic for canonically equivalent map/entity order and detects changed serialized content. It is an **integrity checksum, not an authenticated digital signature**.

`ClientWorldBootstrapReplica.TryInstall` validates the complete candidate and checks expected WorldId and local content manifest before an atomic replace. It rejects stale network/game revisions and allows callers to obtain only defensive copies.

## Relationship to existing systems

- The existing `AuthoritativeProtocol.Version = 1`, `WorldSnapshot`, `WorldDelta` and `ClientStateReplica` remain unchanged and continue to back the loopback movement probe.
- The new schema deliberately lives in separate types in `Source/AuthoritativeCore`. No existing client should decode a v2 bootstrap using the v1 snapshot codec.
- Transport frame negotiation, chunk streaming, world-save checkpoints, Verse map extraction, `Thing` presentation on clients and suppression of every gameplay mutation path remain explicit future work.
- Map-layer representation may exceed the 8 MiB bound in large or heavily modded worlds. Future transport must split into authenticated, checksummed chunks rather than silently lifting memory budgets.

## Test evidence

Run `dotnet run --project Source/AuthoritativeCore.Tests/AuthoritativeCore.Tests.csproj -c Release`. The focused v2 tests cover deterministic encode/decode, terrain/roof/fog dimensions, rejected duplicate IDs, bad domain/coordinates/pawn data, reversed mod load order, changed Def/mod hashes, wrong world, atomic rejected updates, defensive copies and malformed/oversized payloads. The existing socket and baseline tests continue to run in GitHub Actions.

## Next implementation gates

1. Validate the new compiled loaded-mod/Def identity collector against a licensed game and extend it to semantic resolved Def hashing; implement persistent world ID and correct RimWorld 1.6 planet tile/layer mapping. See docs/VERSE_V2_EXTRACTION.md and docs/MOD_CONTENT_FINGERPRINTS.md.
2. Negotiate v2 support explicitly after authentication and before client map startup; publish bounded chunks with a per-checkpoint digest.
3. Build a **read-only** Verse client presentation layer with no client-side jobs, pathfinding, pawn trackers or world ticking. Test against a real game process.
4. Add event-based entity changes and relationships (containers, equipped things, injuries, state-machine transitions) with transaction boundaries, and full server Scribe saves.
5. Only after those acceptance gates, expand to multiple maps/factions and cross-time world interactions.


## Verse-backed map extraction (sixth iteration)

A read-only core capture builder now accepts `IWorldBootstrapMapSource` callbacks and packs real map-layer DTOs. The net48 `VerseSingleMapBootstrapSource` adapter reads terrain/roof/fog and spawned Verse Things/Pawns on the Unity simulation thread. `RimWorldSingleMapDriver.CaptureBootstrapV2` exposes an **explicit** call requiring a real trusted content manifest, world UUID, planet tile mapping and producer revision. It is intentionally not available to network clients and has not been executed in game. Four new pure-core mapping tests cover the packing; see docs/VERSE_V2_EXTRACTION.md.
