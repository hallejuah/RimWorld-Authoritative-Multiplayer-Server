# Iteration 8: server-owned world and entity identity

## Scope and evidence

This iteration implements an identity foundation for Stage A of the Amity-inspired roadmap, not a playable multiplayer release. The target remains RimWorld 1.6 with server-owned Verse gameplay. No reference-mod code, binaries, assets or wire formats are imported.

The existing CI workflow builds the net8/netstandard2.0 core, net48 Verse bridge, local TCP transport, optional probe mod and standalone probe client, then runs core and socket checks. The core suite now contains 47 cases: the original 34 plus 13 identity cases. Successful CI does **not** prove a real Scribe save/reload, Unity thread lifecycle, or client gameplay. Those runtime checks remain required below.

## Implemented components

`ServerEntityRegistry` allocates positive monotonic IDs in a world UUID namespace. A trusted server save-reference key maps to one issued ID. Looking up a command target never allocates a new ID. Retired IDs and keys remain reserved. Restoring builds a new validated registry rather than partially mutating a live one.

`ServerEntityBindings<T>` associates runtime objects with registry keys using reference equality. It rejects two objects with one key and one object changing its key. Runtime references are not serialized; after a trusted reload a new binding epoch reconnects new objects to their previous IDs. This is not an ownership or container graph.

`EntityIdentityCheckpointCodec` serializes bounded metadata in an independent format, validates world identity, version, allocator high-water mark, record uniqueness, byte lengths, flags and UTF-8, and checks a trailing SHA-256 digest. Decoding verifies and parses the same defensive byte copy. The digest detects accidental corruption; it is not a signature, an authenticated network protocol or a durability guarantee.

`GameComponent_AuthoritativeIdentity` stores metadata inside the ordinary server Scribe save. Its fields are `authoritativeIdentityRequired`, `authoritativeWorldId` and `authoritativeEntityIdentities`. The payload contains the registry version, world UUID, next-ID counter and all active/retired records. There is no sidecar file that can silently be paired with a different save.

`RimWorldSingleMapDriver` and `VerseSingleMapBootstrapSource` use the same component for v1 pawn snapshots, v2 entity snapshots and MovePawn resolution. Network IDs no longer come directly from `thingIDNumber`. `GetUniqueLoadID()` is used only as a trusted server save-reference. The existing bootstrap overload accepting a UUID rejects a mismatch with the loaded save; a new overload reads the UUID from the component.

## Thread and save lifecycle

The Scribe loading thread decodes an immutable checkpoint. The live registry is created on first use by the trusted loaded-game adapter, so it belongs to the simulation thread rather than the loading thread. Subsequent runtime lookups and mutations are thread-affine. Saving before the first host session preserves the pending checkpoint without binding the future runtime to the saving thread.

Once a live registry exists, saving its identity data must run on that owner thread. Background saving of a live registry is not supported. The required-marker field is written before capture: if an enclosing saver catches a component exception yet completes a save, absent identity fields with that marker must be rejected on reload rather than treated as legacy data. Actual Scribe exception behavior still needs the runtime check below; this is not a whole-save transactional implementation.

Only a bound Thing whose `Destroyed` flag is set is retired at save capture. Absence from a map, despawning, inventory placement or caravan travel is not evidence of destruction. A retirement is a permanent reservation, not deletion of the registry record. Objects not observed during the current binding epoch are retained conservatively.

A legacy save with no adoption marker and neither identity field receives a UUID on first server use or save. Partially present, empty, malformed or mismatched data sets a fault and blocks authoritative identity access. Marked saves cannot silently fall back to legacy adoption. Deliberately removing the entire component is still indistinguishable from a genuinely old save: save files are trusted server input, not client uploads.

## Limits and compatibility

- Maximum 100,000 identities per world, including retired records; UTF-8 source keys at most 256 bytes; encoded metadata at most 32 MiB. Retained runtime references are likewise bounded by the registry lifetime limit and released with the game. Compaction and larger-world policy are future work.
- IDs are scoped by the world UUID, not globally unique bare integers. `long.MaxValue` is an exhausted allocator sentinel. Keys are case-sensitive and reject empty, padded, control-character and invalid-encoding values.
- Persisted metadata is authoritative only at a successful save boundary. Restoring an older full save can roll back the allocator. Copying a whole save copies its UUID. No external world-epoch authority, rollback prevention, crash journal or exactly-once replay is implemented.
- Faction/domain IDs, world state revisions and command receipts have not become durable. Object holders, parent links, equipment and deep pawn state remain future DTO/graph work. The registry does not grant permissions or authorize owner migration.
- v1 remains a localhost-only test protocol, without a world UUID in every command. Old debug commands that used Verse numeric IDs are not a supported migration path: start fresh probe sessions and select IDs from the current snapshot. Production v2 world/epoch negotiation and replay protection remain required.
- Capture remains restricted to one loaded, paused map. It may allocate identity metadata but never spawns Things or executes client gameplay.

## Automated checks

The 13 new cases cover allocation and nonallocating lookup, reload ordering, retirement, key/capacity/overflow limits, invalid and foreign checkpoints, binding collisions, reference equality and rebind, thread affinity, loading-to-simulation handoff, truncation/corruption, structurally invalid input with a valid checksum, defensive copies/canonical encoding, and deterministic randomized recovery (three seeds, 1,200 operations each).

Run from the repository root with the existing SDK/package prerequisites:

```sh
dotnet build Source/AuthoritativeCore/AuthoritativeCore.csproj -c Release --warnaserror
dotnet build Source/AuthoritativeRimWorldBridge/AuthoritativeRimWorldBridge.csproj -c Release
dotnet build Source/AuthoritativeProbeMod/AuthoritativeProbeMod.csproj -c Release
dotnet run --project Source/AuthoritativeCore.Tests/AuthoritativeCore.Tests.csproj -c Release
dotnet run --project Source/AuthoritativeTransport.Tests/AuthoritativeTransport.Tests.csproj -c Release
```

The complete CI workflow also builds both transport targets and the standalone probe client and stages the isolated probe mod. Compilation uses reference assemblies; it is not execution of RimWorld.

## Required RimWorld 1.6 runtime acceptance (not yet executed)

1. Back up a disposable save. Load one map, pause vanilla time, start the isolated probe host and record the component's UUID, issued pawn IDs and v2 entity IDs. Verify the same pawn uses one ID in both projections; submit a MovePawn command using that ID and confirm faction/map validation remains intact.
2. Stop the host, save on the owner thread, fully reload the game, restart the host and compare UUID and IDs. Verify a new entity gets an unused ID. Repeat with a different enumeration/capture order. The test must include the actual Scribe loader-to-Unity-thread handoff.
3. Despawn or move a test item into a holder, then respawn it: its identity must survive. Destroy a bound object, save/reload and verify its old key/ID cannot be reused. These checks validate identity only, not a new inventory replication feature.
4. In copies of the save, corrupt the Base64 payload, remove one identity field, change the outer UUID, or leave the required marker while removing both identity fields. Authoritative host activation must fail without reminting IDs. Try saving the faulted game and reloading its output: the required marker must keep it faulted, even if the enclosing Scribe saver catches the component exception. Also verify a truly unmarked legacy save can be adopted.
5. Confirm the normal same-thread save path succeeds and a foreign-thread save of an active registry fails closed. Observe allocations and retained references on a representative large save before raising any limits.

Do not use an irreplaceable colony or declare the Scribe integration game-tested until these checks pass.

## Reference supplied for this iteration

The supplied `3832944039.zip` was inspected as an architectural reference. Its top-level archive entries contain client assemblies, XML and assets, with no C# source project or server implementation. No DLL was executed or added to this repository. Binary fingerprints identify the inspected inputs, not a compatibility promise:

```text
Amity.dll         SHA256 82301c81f7b925837779c8fbc07c2720468c7b98ea657621eadc781e6eaf0528
Amity.Protocol.dll SHA256 503147f26bf8fb0d1b8bffb94f41b71414a2f01bb90c8c6d3738b99c56202113
```

## Next engineering slice

Build bounded Pawn/Thing/Container relationships on top of these identities, with explicit holder references and atomic validation. Then connect the v2 bootstrap to chunked transport and a read-only client presentation. Full checkpoint durability, authenticated world/epoch sessions and runtime acceptance must not be inferred from the identity foundation.
