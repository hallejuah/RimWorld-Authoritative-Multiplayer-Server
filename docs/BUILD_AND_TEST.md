# Build and test — authoritative first iteration

## Baseline

Base Multiplayer commit: 4a3be276bbf90cc597abfa5b299935ca8eeeb285.
Authoritative code is intentionally separate from Source/Multiplayer.sln and the existing 0.11.5 client and standalone relay server; it currently targets .NET 8 only.

Dependencies for the standalone core: .NET 8 SDK; no NuGet dependencies.
Dependencies for the original client: .NET SDK/MSBuild for net48, Krafs.Rimworld.Ref 1.6.4566, Harmony 2.4.1, and other packages specified in the csproj; for a genuine game integration run, licensed RimWorld 1.6 game files, Unity runtime and Prepatcher are additionally required.

Run independently:
- dotnet build Source/AuthoritativeCore/AuthoritativeCore.csproj -c Release
- dotnet run --project Source/AuthoritativeCore.Tests/AuthoritativeCore.Tests.csproj -c Release

The test runner exits nonzero on failure and uses no mocking library. Its small test-only ICommandExecutor is not a substitute for RimWorld gameplay.

For upstream baseline (may require restoring dependencies and platform-specific build tools):
- dotnet build Source/Multiplayer.sln -c Release

## What this environment verified

GitHub files, master revision, project references, LICENSE files and C# sources were read through the connected GitHub API. GitHub writes were used to create a dedicated feature branch and commits.

The current execution sandbox does NOT have a dotnet SDK, Mono C# compiler, MSBuild or locally accessible game DLLs. Direct git clone was unavailable because container DNS could not reach github.com. Therefore no dotnet compilation, unit-test executable or actual RimWorld session has run here. Do NOT infer build success from these source additions.

## Test scenario coverage (unit, code present; execution pending)

- Deduplication of retransmitted commands; conflict on ID reuse.
- Reject unauthorized faction, unauthorized domain, spoofed session, stale revision and unsupported speed.
- Atomic snapshot + state deltas; creation, movement update, removal.
- Detect missing versions, unknown IDs and invalid duplicate snapshot IDs.
- Typed command/snapshot/delta serialization and message-size enforcement.
- Independent domain clocks, pause, save/restore of clock metadata, duplicate domain IDs.
- Reject dispatch on a different thread and preserve clock state on a failed tick.

## Integration work still required

A licensed RimWorld 1.6 installation and Unity server launcher are required to test a pawn's real server-side movement, a snapshot-to-Thing presentation bridge, client gameplay tick suppression, map creation, health/jobs and reconnect after a network failure. None of these integration tests has been executed.

No benchmark, Docker-based RimWorld runtime, working game networking, or full save/load has been demonstrated.
