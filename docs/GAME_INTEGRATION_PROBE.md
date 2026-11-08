# RimWorld 1.6 single-map integration probe

Status: source-compiled bridge, **NOT executed with a real RimWorld/Unity runtime**. This is a reproducible manual game integration plan, not a report of successful gameplay.

## Compile prerequisites

Use .NET SDK to build:
```bash
dotnet build Source/AuthoritativeCore/AuthoritativeCore.csproj -c Release -f netstandard2.0
dotnet build Source/AuthoritativeRimWorldBridge/AuthoritativeRimWorldBridge.csproj -c Release
```

Source/AuthoritativeRimWorldBridge is a separate net48 assembly. Compilation uses Krafs.Rimworld.Ref (RimWorld 1.6.4566) and Lib.Harmony 2.4.1 compile references. It does not include Ludeon binaries, content, or HeadlessRim GPL code. For actual execution, prepare a licensed RimWorld 1.6 installation with Unity, Harmony and Prepatcher where needed. Resolve all transitive assemblies required by the netstandard2.0 core/System.Text.Json before loading the bridge. The repository deliberately does not ship or automatically install these binaries.

## Actual experiment to run in a licensed game

1. Arrange for both authoritative assemblies and their compatible runtime dependencies to be loaded in a separate experimental server-side RimWorld mod profile. Do **not** overwrite existing Multiplayer DLLs or enable the experiment in a normal lockstep session.
2. Start RimWorld 1.6, load one vanilla/Core colony and keep the game paused. There must be exactly one loaded Map, with Current.ProgramState == Playing and the code running on the Unity thread.
3. Supply a real ICommandAuthorization implementation bound to a previously authenticated network session. It must map the player's identity to their faction and validate ownership of MapDomainIds.FromMapUniqueId(map.uniqueID). No production authentication integration exists yet.
4. On the Unity thread, call LoadedSingleMapHost.Attach(map, factionId, authorization) and BeginReplication(). Inspect the returned WorldSnapshot, which contains only positive-ID spawned faction pawns and their initial coordinates.
5. Call SingleMapUnityTickLoop.Start(loadedHost). Its Harmony TickManagerUpdate prefix suppresses vanilla automatic ticking and its Root_Play.Update postfix pumps the authoritative host once per Unity frame. Explicitly set loadedHost.Host.Domains.SetSpeed(loadedHost.Driver.DomainId, 1). Ensure the single-map server state ticks while the default RimWorld time control remains paused.
6. From a simulated/test transport worker (not the Unity thread), enqueue a PlayerCommand.MovePawn with a new GUID, authenticated session identity, correct faction/domain and the latest loadedHost.Driver.StateVersion. Target a valid owned pawn and walkable cell. Do not block the Unity thread waiting for its Task; wait asynchronously for the command receipt.
7. Observe the actual pawn receiving a Verse Goto job and moving on subsequent server-side ticks. Read loadedHost.CaptureDelta() on the Unity thread and check the ordered position changes against a separate ClientStateReplica initialized from the baseline snapshot.
8. Call SingleMapUnityTickLoop.Stop; verify the normal game remains paused and a second activation does not duplicate Harmony patches. Test again from a saved game with a nonzero TickManager.TicksGame and with map uniqueID zero.

## Required evidence before accepting Stage 1

Collect real Unity/RimWorld logs, tick counter and pawn positions, evidence of one and only one game tick path, a network trace carrying a snapshot and deltas, and a real client that renders them **without running local gameplay simulation**. Exercise reconnect from a new snapshot and compare server and client object references. Run Windows and Linux game integration separately.

## Known blockers that this source does not overcome

- There is a loopback-only TCP prototype and a caller-provided ILoopbackAuthenticator; there is still no TLS-protected remote transport, authenticated production session policy or public server.
- There is no deployed headless Unity bootstrap or automatic activation path. The bridge is currently opt-in library code only.
- This first single-map driver calls global TickManager.DoSingleTick; it cannot implement two independent map clocks.
- There is no client presentation bridge that can load the snapshot into Verse maps/Thing objects.
- There is no full Scribe save coordinator, persistent command UUID journal, stable network entity ID registry or cross-domain transaction policy.
- Client UI and other mods may cause gameplay side effects outside TickManagerUpdate; suppressing the tick method alone does not prove read-only clients.
- Runtime availability of proprietary RimWorld assemblies and all bridge dependencies has not been verified; only reference-based compilation exists.


## Optional loopback transport probe

Once the game is loaded, one map is paused, and LoadedSingleMapHost.Attach plus SingleMapUnityTickLoop.Start were called on Unity's main thread, the embedding experimental mod may call:

```csharp
// Supply a secure LOCAL test credential provider; no provider is shipped.
// Do not use the fake token from Source/AuthoritativeTransport.Tests.
int port = loadedHost.StartLocalTransport(localAuthenticator);
```

A separate local process can then call LoopbackTransportClient.ConnectAsync(port, credential), consume the initial DTO snapshot, issue PlayerCommand.MovePawn with ExpectedStateVersion = client.Replica.GameStateVersion, and PollAsync to apply deltas. The Unity thread must remain pumping. Run the client asynchronously; never block Unity waiting for command receipts. Stop with SingleMapUnityTickLoop.Stop, which closes the transport.

This remains a manual integration plan and an optional library hook. There is NO built/deployed game mod installer, no shipped production authenticator and no end-to-end game test report. Do not expose or port-forward this cleartext loopback service.
