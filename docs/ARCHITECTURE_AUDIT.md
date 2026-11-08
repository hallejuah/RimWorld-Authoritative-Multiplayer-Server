# Architecture audit — RimWorld 1.6 Multiplayer

Status: code audit complete enough to identify the first integration boundary; executable RimWorld integration NOT verified.
Audited revision: rwmt/Multiplayer master commit 4a3be276bbf90cc597abfa5b299935ca8eeeb285 (version 0.11.5, April 29, 2026).
Fork at start: hallejuah/RimWorld-Authoritative-Multiplayer-Server master at the same commit.
The rwmt/dev branch was observed at ddfccdbc9538724fd1dd97146e81ddae8fecef69 but was not selected without confirming its RimWorld 1.6 compatibility.

## Real architecture and call sites

- Source/Multiplayer.sln contains Client, Common, Server, Tests, MultiplayerLoader and TestsOnMono.
- Source/Client/Multiplayer.csproj targets net48 and references Krafs.Rimworld.Ref 1.6.4566 and Lib.Harmony 2.4.1.
- About/About.xml declares RimWorld 1.6 and Prepatcher as a required mod; its description requires RimWorld >= 1.6.4491.
- Source/Server/Server.cs creates MultiplayerServer, handles settings/ZIP join-point data and launches its Run loop.
- Source/Common/MultiplayerServer.cs: Run and TickNet advance a command-delivery game timer and send time-control packets. This is NOT a Verse or RimWorld simulation loop.
- Source/Common/Networking/State/ServerPlayingState.cs: HandleClientCommand sends commands via Server.commands.Send. It contains an explicit TODO for validating a player's map. HandleSetFaction also carries an explicit TODO for restricting access.
- Source/Common/Networking/Packet/CommandPacket.cs defines the original lockstep command transport, with no per-command UUID or server-authored state delta.
- Source/Client/Networking/State/ClientPlayingState.cs: HandleCommand schedules received commands locally; HandleMapResponse reloads saved map content.
- Source/Client/Patches/TickPatch.cs: Harmony patches TickManager.TickManagerUpdate; RunCmds and DoTick/DoUpdate run local gameplay ticks. This is the critical client simulation path.
- Source/Client/AsyncTime/AsyncTimeComp.cs: Tick executes MapPreTick, TickLists, Storyteller, quests, MapPostTick and manager updates. It swaps contextual global state and RNG.
- Source/Client/AsyncTime/AsyncWorldTimeComp.cs: Tick invokes Find.TickManager.DoSingleTick; DesiredTimeSpeed derives world speed from the fastest active map, not an independent persistent global clock.
- Source/Client/Patches/MapSetup.cs: SetupMap creates MultiplayerMapComp and AsyncTimeComp and builds faction-specific state.
- Source/Client/Factions/FactionCreator.cs: CreateFaction is a SyncMethod and GenerateNewMap calls GetOrGenerateMapUtility.GetOrGenerateMap inside long-event handling. Currently it runs in synchronized client simulation, not an authoritative server.
- Source/Client/Patches/UniqueIds.cs uses local negative ID overrides for UI/entry contexts; generated game IDs cannot be blindly treated as stable network entity identities.
- Source/Client/Saving/SaveLoad.cs and Source/Client/Saving/Loader.cs use Scribe/XML and map reload/replay. Source/Client/Session/Rejoiner.cs returns the user to loading and reconstructs world state.

## Reuse, change and replace

Reuse as reference: map lifecycle sequencing, existing faction-data partitioning, SyncMethod command definitions as an inventory of player intentions, tick-context/RNG isolation, map serialization patterns, LiteNetLib framing concepts, and existing diagnostics.

Change: the command admission path (authenticated session and faction authorization), lifecycle ownership (server alone generates maps), time-domain ownership (server schedules maps), and client update entrypoints (read-only presentation/visuals).

Replace for authoritative mode: command replay as the normal source of state; distributed RNG agreement as a correctness requirement; client-side Verse gameplay ticks; host-uploaded world snapshots; full replay from command history after reconnect.

Existing components to isolate rather than run unchanged: TickPatch, AsyncTimeComp, AsyncWorldTimeComp, FactionCreator, SaveLoad, Loader and Sync serialization. They depend on Verse/Unity globals, player context and main-thread behavior.

## Global-state and threading obstacles

AsyncTimeComp.Tick mutates Find.TickManager.ticksGameInt, map-specific managers and global faction/RNG context. AsyncWorldTimeComp.Tick invokes TickManager.DoSingleTick and iterates maps for faction setup. FactionCreator.GenerateNewMap uses Find, Current.Game and Unity-related long events. These are concrete reasons NOT to invoke separate map ticks from arbitrary worker threads.

Headless operation requires a properly licensed RimWorld installation and, unless proven otherwise, its Unity runtime on the simulation thread. The existing standalone net8 server has no game runtime.

## Desync interpretation

Wrong random state on map 0 and Map instances don't match were reported by the project owner. Neither diagnostic ZIP is present in this task environment. FactionCreator and MapSetup are plausible audit targets, but source inspection alone does not establish a specific desync root cause.

## External projects and licensing

- rwmt/Multiplayer: LICENSE is MIT (Copyright 2018 Zetrith); preserve notice.
- AantCoder/OnlineCity: LICENSE is Apache-2.0. It is a world/faction/transactions reference; its historical README describes server synchronization and periodic saves, not an authoritative RimWorld 1.6 simulation.
- IlyaChichkov/HeadlessRim: LICENSE is GPL-3.0; README describes a Harmony headless layer with RimWorld/Unity runtime. No HeadlessRim code was copied or linked into this MIT fork.
- No proprietary RimWorld binaries, game sources, or assets are added.

## Minimal credible game experiment (NOT implemented yet)

1. Provision a licensed RimWorld 1.6 installation plus Harmony and Prepatcher in a Unity batchmode-capable server environment.
2. Bootstrap Verse, Current.Game and one Map on the server Unity main thread, using known 1.6 APIs.
3. Advance exactly one authoritative map tick through a verified engine entrypoint, log pawn position/state and save/reload with Scribe.
4. Build a client startup path that loads a server snapshot yet suppresses TickPatch gameplay execution while preserving rendering/input.
5. Submit an authenticated move order to the server-side executor; replicate pawn coordinates from server state and render without running local AI.
6. Disconnect and reconnect to a fresh snapshot; compare server state and client presentation.
7. Instrument global Find, Rand, faction, pathfinder and long-event interactions; do not add worker-thread ticking until each is proven safe.

The new Source/AuthoritativeCore implements limited transport-neutral foundations only. It does not implement these seven game steps.
