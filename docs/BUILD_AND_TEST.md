# Build and test — authoritative first iteration

## Baseline

Base Multiplayer commit: 4a3be276bbf90cc597abfa5b299935ca8eeeb285.
Authoritative code is intentionally separate from Source/Multiplayer.sln and the existing 0.11.5 client and standalone relay server. AuthoritativeCore targets both net8.0 and netstandard2.0; the opt-in RimWorld bridge targets net48.

Dependencies: .NET 8 SDK; the core netstandard2.0 target restores System.Text.Json 8.0.5. The bridge references Krafs.Rimworld.Ref 1.6.4566, Krafs.Publicizer 2.3.0 and Lib.Harmony 2.4.1 at build time. Compilation uses reference assemblies; game execution additionally needs licensed RimWorld binaries and Unity.
Dependencies for the original client: .NET SDK/MSBuild for net48, Krafs.Rimworld.Ref 1.6.4566, Harmony 2.4.1, and other packages specified in the csproj; for a genuine game integration run, licensed RimWorld 1.6 game files, Unity runtime and Prepatcher are additionally required.

Run independently:
- dotnet build Source/AuthoritativeCore/AuthoritativeCore.csproj -c Release
- dotnet build Source/AuthoritativeCore/AuthoritativeCore.csproj -c Release -f netstandard2.0
- dotnet build Source/AuthoritativeRimWorldBridge/AuthoritativeRimWorldBridge.csproj -c Release
- dotnet run --project Source/AuthoritativeCore.Tests/AuthoritativeCore.Tests.csproj -c Release

The test runner exits nonzero on failure and uses no mocking library. Its small test-only ICommandExecutor is not a substitute for RimWorld gameplay.

For upstream baseline (may require restoring dependencies and platform-specific build tools):
- dotnet build Source/Multiplayer.sln -c Release

### Local transport and socket integration

- dotnet build Source/AuthoritativeTransport/AuthoritativeTransport.csproj -c Release
- dotnet run --project Source/AuthoritativeTransport.Tests/AuthoritativeTransport.Tests.csproj -c Release

The experimental TCP listener only binds localhost and MUST NOT be exposed to LAN/WAN. See docs/LOOPBACK_TRANSPORT.md.

## What this environment verified

GitHub files, master revision, project references, LICENSE files and C# sources were read through the connected GitHub API. GitHub writes were used to create a dedicated feature branch and commits.

The interactive execution sandbox does NOT have a dotnet SDK, Mono C# compiler, MSBuild or locally accessible game DLLs. Direct git clone was unavailable because container DNS could not reach github.com. Therefore no local compilation or actual RimWorld gameplay integration has run here.

GitHub Actions DID run the projects on hosted runners:
- Isolated core workflow: https://github.com/hallejuah/RimWorld-Authoritative-Multiplayer-Server/actions/runs/42481107400 — .NET 8 Release build succeeded with zero warnings and errors; all 11 first-revision standalone core scenarios passed. The later authoritative host/queue revision passed 14/14 focused tests in https://github.com/hallejuah/RimWorld-Authoritative-Multiplayer-Server/actions/runs/42481559461 (0 warnings, 0 errors).
- Existing solution PR workflow: https://github.com/hallejuah/RimWorld-Authoritative-Multiplayer-Server/actions/runs/42481107485 — original solution Debug and Release builds passed (68 and 78 existing warnings, respectively; zero errors), and 85 existing tests passed, zero failed/skipped.

These are real automated build/test results, but neither workflow starts RimWorld or exercises Unity integration.

## Test scenario coverage (isolated automated tests; verified on GitHub Actions)

- Deduplication of retransmitted commands; conflict on ID reuse.
- Reject unauthorized faction, unauthorized domain, spoofed session, stale revision and unsupported speed.
- Atomic snapshot + state deltas; creation, movement update, removal.
- Detect missing versions, unknown IDs and invalid duplicate snapshot IDs.
- Typed command/snapshot/delta serialization and message-size enforcement.
- Independent domain clocks, pause, save/restore of clock metadata, duplicate domain IDs.
- Reject dispatch on a different thread and preserve clock state on a failed tick.
- Bounded transport-thread command queue, simulation-thread host pump and overload rejection.

## Integration work still required

A licensed RimWorld 1.6 installation and Unity server launcher are required to test a pawn's real server-side movement, a snapshot-to-Thing presentation bridge, client gameplay tick suppression, map creation, health/jobs and reconnect after a network failure. None of these integration tests has been executed.

No benchmark, Docker-based RimWorld runtime, working game networking, or full save/load has been demonstrated.


## Second-iteration compile verification and smoke-test setup

- net8.0, netstandard2.0 and the new net48 Verse bridge compiled through .github/workflows/authoritative-core.yml.
- The separate bridge is intentionally excluded from Source/Multiplayer.sln and not copied to the deployed mod. This prevents half-integrated gameplay changes from affecting the original lockstep multiplayer.
- The bridge calls real RimWorld API entry points, but the CI references are not an installed or running game. No one has executed a licensed RimWorld gameplay session in this environment.

For an in-game proof, follow docs/GAME_INTEGRATION_PROBE.md. Do not mark the first vertical slice accepted until map loading, ticking, pawn job execution, client rendering WITHOUT local game ticks, and reconnect are observed with an actual game process.


## Third iteration — real TCP tests

An additional independently compiled net8/netstandard2.0 transport project provides bounded length-prefixed loopback-only frames, application-supplied authentication, snapshot on connect, command receipt, delta polling and snapshot-based resync on an expired history window.

GitHub Actions successfully executed the 7 socket-level tests on Source/AuthoritativeTransport.Tests, including initial handshake, authenticated command queue delivery on the simulation thread, fake-pawn location replication, duplicate retry, reconnect snapshot, wrong-credential/session/faction rejection, state-history expiry and invalid frame limits. The tests use real TCP sockets but a fake game driver, NOT a licensed RimWorld/Unity process. The source compiler also checks the Verse bridge project against RimWorld 1.6 reference assemblies.

For current status see the GitHub Actions run linked from pull request #1. To test against a real loaded game, see docs/GAME_INTEGRATION_PROBE.md.
