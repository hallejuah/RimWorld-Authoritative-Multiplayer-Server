# WorldBootstrap v2 — independent server-owned presentation bootstrap

**Status:** source-implemented and CI-tested DTO schema, validation, bounded JSON codec and atomic client admission. **NOT populated from an actual Verse game; NOT sent by the current v1 local TCP probe.**

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

**Critical:** No Verse manifest collector or mod-content hashing is implemented yet. A real game integration MUST derive hashes from actual loaded code/assets and resolved definitions. Do not construct dummy hashes or trust the joining client's declared values.

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

1. Define a trustworthy server-side, single-map Verse extractor that fills every v2 field using actual loaded game data and computes resolved content hashes; prove output matches a licensed test game.
2. Negotiate v2 support explicitly after authentication and before client map startup; publish bounded chunks with a per-checkpoint digest.
3. Build a **read-only** Verse client presentation layer with no client-side jobs, pathfinding, pawn trackers or world ticking. Test against a real game process.
4. Add event-based entity changes and relationships (containers, equipped things, injuries, state-machine transitions) with transaction boundaries, and full server Scribe saves.
5. Only after those acceptance gates, expand to multiple maps/factions and cross-time world interactions.
