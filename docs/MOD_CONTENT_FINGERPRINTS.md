# Loaded-mod file trees and runtime Def identity fingerprints

**Status:** compiled and tested on Linux CI using fixture directories; net48 Verse adapter compiles against RimWorld 1.6 reference assemblies, but has **NOT run in a licensed game/Unity process**. No real game join gate uses these fingerprints yet.

## Goal and trust boundary

The authoritative process must compare *the server's actual game installation* with the joining client's active mods and resolved Def database before allowing a world bootstrap. The contents of an unauthenticated client's supplied manifest are not proof of the server installation.

This milestone creates two independently calculated SHA-256 values:

1. Each loaded mod's **complete directory file tree** — contents of every regular file plus canonical relative path, in deterministic order. This is intentionally conservative and may reject compatible installations when irrelevant files differ.
2. **Resolved Def identity set** — type full name, defName and 16-bit shortHash for all enumerated `DefDatabase<T>` values. This detects missing, renamed, or differently assigned Def IDs, **but does not verify the semantic fields of Def instances**. Two installations can have identical identity hashes while runtime Def values differ.

The wire schema currently names the second digest `DefCatalogSha256`. **Treat it as an identity-only compatibility hint, NOT proof that all runtime Def semantics match.** A later protocol must explicitly version the digest scope, hash canonical resolved Def field values and verify mod assembly behavior.

## File tree SHA-256 v1

`ContentFingerprintBuilder.HashModDirectory(path, limits, cancellationToken)` reads a trusted mod directory. It includes all regular files, such as About.xml, XML, assemblies, images, asset bundles, documentation, even source folders and repository metadata if present.

Encoding:
- UTF-8 ASCII header `RW-AUTH-MOD-FILETREE-v1\0`;
- enumerate entries recursively, reject directory/file symlinks and reparse points, reject Unicode non-NFC names and case-insensitive path collisions;
- canonical relative paths use `/`, sorted by ordinal comparison; no absolute machine paths or file timestamps enter the hash;
- for each file append its relative UTF-8 path (little-endian 64-bit length and bytes), its 64-bit file size and its raw 32-byte SHA-256 content digest;
- compute final SHA-256 of the concatenated records and expose lowercase 64-digit hex.

The implementation streams file contents instead of reading them entirely into RAM. Every file is checked for concurrent length/timestamp changes before and after hashing; file-system races cannot be eliminated completely without an immutable snapshot. Root ancestors are checked for reparse points.

Default fail-closed limits per mod: 100,000 files, 32 directory levels, 512 MiB per file, 4 GiB total bytes, 1024 UTF-8 bytes per relative path. Mod directories with zero files fail. The limits are a memory/IO budget, **not a performance guarantee**.

**Caution:** This intentionally hashes *all* files, including irrelevant documentation, so different packaging of otherwise gameplay-compatible mods may produce mismatches. Hashing all loaded mod roots can be expensive. Read the active mod order and Def identity list on the Unity thread, then run `HashOnWorker()` **outside the Unity tick loop**, once per setup/checkpoint, never every frame.

## Runtime Verse adapter

`RimWorldSingleMapDriver.CaptureManifestInputs()` requires the existing single loaded paused game on the owner thread and returns `VerseContentManifestInputs`. That collector reads:

- `LoadedModManager.RunningModsListForReading` in **actual active order**, using each content pack's `PackageId` and `RootDir`;
- `About/About.xml` optional `modVersion` with DTD prohibited and XML document size limited; missing `modVersion` is reported literally as `unspecified`, not invented;
- a RimWorld `VersionControl` value, extracting its numeric 1.6 version; if unavailable, fail closed;
- `GenDefDatabase.AllDefTypesWithDatabases` and the corresponding live `DefDatabase<T>.AllDefsListForReading` collections, including dynamically registered Def categories. The code reads **identities** and their short hashes rather than serializing the game object graph.

These inputs are copied to standalone managed records before hashing. Calling `VerseContentManifestInputs.HashOnWorker()` does not itself access Verse or Unity; only file system data and copied identity records are read. Unhandled reflection/API changes or missing loaded mods cause errors instead of placeholder hashes.

## Def identity SHA-256 v1

`ContentFingerprintBuilder.HashResolvedDefIdentities` sorts by ordinal TypeFullName then DefName. Duplicate (type, name) pairs are rejected. It serializes the `RW-AUTH-RESOLVED-DEF-IDENTITIES-v1\0` header followed by length-prefixed names and a little-endian 64-bit encoding of each shortHash. The final digest is SHA-256.

This digest is stable against enumeration order, but not equivalent to a full canonical serialization of resolved Defs, mod patches, settings or runtime behavior.

## Test commands

```bash
dotnet build Source/AuthoritativeCore/AuthoritativeCore.csproj -c Release --warnaserror
dotnet build Source/AuthoritativeRimWorldBridge/AuthoritativeRimWorldBridge.csproj -c Release
dotnet run --project Source/AuthoritativeCore.Tests/AuthoritativeCore.Tests.csproj -c Release
```

Four new pure-core tests verify stable order and timestamps, changed files and paths, failure on over-budget content and symbolic links, changed Def identity/shortHash, loaded mod ordering, cancellation and rejection of malformed versions or duplicated package IDs.

**No automated test has executed `CaptureManifestInputs` under a licensed game.** The code is not activated by the experimental probe, and v2 bootstrap is not connected to a network endpoint.

## Before claiming real compatibility

- Capture and compare *semantic* resolved Def fields, including all implied Defs and patch results, with a versioned canonical representation.
- Establish whether actual loaded assemblies, subfolders selected for RimWorld 1.6, runtime patch configuration, and mod settings differ even when their installed file trees match. Hashing files does not prove equivalent runtime behavior.
- Test the 1.6 reflection-based Def database enumeration and runtime version extraction against an installed game.
- Add separate authenticated client handshake and explicit server-derived manifest policy; no client can choose the authoritative hashes.
- Verify on Windows and Linux that file path case, Unicode and Steam Workshop packaging produce the same intended constraints.
