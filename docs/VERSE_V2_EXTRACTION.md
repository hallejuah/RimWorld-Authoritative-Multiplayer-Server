# RimWorld 1.6: Verse-backed WorldBootstrap v2 capture

Status: source compiled against RimWorld 1.6.4566 references, pure map-packing tested in core; **NOT run in a licensed RimWorld game.** No automatic startup and no v2 TCP dispatch.

## New code

- `Source/AuthoritativeCore/WorldBootstrapCapture.cs` defines `IWorldBootstrapMapSource` and creates an atomic bounded, validated `WorldBootstrapV2` from one or more read-only map sources. Its own code never calls Verse.
- `Source/AuthoritativeRimWorldBridge/VerseSingleMapBootstrapSource.cs` is a concrete **read-only Verse map reader** that accesses real `map.terrainGrid.TerrainAt`, `map.roofGrid.RoofAt`, `map.fogGrid.IsFogged`, and `map.listerThings.AllThings`. It reads each spawned Thing's Def/ID/rotation/stack/faction/position; for pawns it additionally reads Drafted, Downed, Dead and the current job Def.
- `RimWorldSingleMapDriver.CaptureBootstrapV2(worldId, worldTile, authoritativeManifest, networkVersion)` connects the loaded single map to the builder. The game version is read from `StateVersion`, never synthesized from a TCP client's claims.

## Preconditions

The game must be playing on the **Unity thread**, paused at vanilla speed with exactly one loaded map. `WorldId` must be a nonempty server-owned UUID that is persistently bound to the world across genuine save/load; this persistence is **not implemented**. `networkVersion` must be assigned by the producer's publication scheduler (not a client).

**The most important missing prerequisite is a trusted `ContentManifest`.** It must contain genuine loaded RimWorld/mod code/content fingerprints and a real resolved Def catalog digest. These are **not yet computed** by the bridge. Calling the method with arbitrary or placeholder hashes would not be a security or compatibility proof. For this reason, the optional test mod deliberately does not expose a `CaptureBootstrapV2` button or wire endpoint yet.

**WorldTile** is supplied explicitly. RimWorld 1.6 uses a `PlanetTile`/world-layer representation, whereas the provisional v2 DTO uses a plain integer. The host must implement and persist an unambiguous tile/layer mapping before a real client can trust this field. No `GetHashCode`-based fake tile mapping is permitted.

## Encoding rules

- For each cell (x,z), `offset = z * Width + x`. A first-encountered terrain Def name receives zero-based byte palette index; roof Def names receive index 1..255 (0 means no roof).
- Fog is a bitset with bit `offset % 8` at byte `offset / 8`.
- Every spawned Thing with a positive `thingIDNumber` is included as a presentation Entity, not a full Verse Thing graph. Nonpositive or duplicate IDs fail the capture. The ID is only **provisional**, not yet verified stable across real save/load.
- The game-thread reader fails closed if map dimensions, missing definitions, invalid factions, entity positions, or transport message size violate the v2 schema. It does NOT skip silently invalid spawned objects.
- `WorldBootstrapValidator.ValidateAndCopy` atomically checks the result and makes defensive copies before it is publishable; `WorldBootstrapCodec.Encode` checks the 8 MiB maximum.

This representation is intentionally **not** a Scribe save, and the field set is far too small to reconstruct a real player-visible, interactive RimWorld map.

## Automated verification

Run:
```bash
dotnet build Source/AuthoritativeRimWorldBridge/AuthoritativeRimWorldBridge.csproj -c Release
dotnet run --project Source/AuthoritativeCore.Tests/AuthoritativeCore.Tests.csproj -c Release
```

Four new pure-core source-adapter tests check terrain/roof palette assignment, fog bits, server version preservation, deterministic ordering, duplicate/unmapped/out-of-bounds IDs, too many palette definitions, invalid dimensions and rejecting bad manifests before any map is read. These tests use a deliberately simple FAKE map source and do **not** demonstrate that live Verse extraction works.

The compiled bridge also fixes a previous false-positive `MovePawn` receipt: if `pawn.jobs.TryTakeOrderedJob(job)` returns false, the driver now rejects the command instead of bumping the authoritative revision.

## Next stages (game-runtime tests remain deferred at the user's request)

1. Implement a trustworthy manifest builder from exactly the loaded RimWorld mods and resolved Def catalog. Add explicit hash scope, normalization and bounded file I/O; verify with fixtures; do not silently invent hashes.
2. Redesign `WorldTile` for RimWorld 1.6 planet layers, stable server save world ID and stable full entity identity.
3. Add authenticated v2 negotiation and bounded streamed/chunked snapshot transport; no clients should mix v1/v2 messages.
4. Build read-only Verse *presentation* from accepted DTOs without ticking client game rules.
5. Validate the real game-thread extraction and client presentation in a licensed RimWorld/Unity installation before claiming Stage 1 accepted.
