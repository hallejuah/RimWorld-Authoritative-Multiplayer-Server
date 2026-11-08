# First manual RimWorld 1.6 probe: captured runtime failure and fix

**Date:** 2026-11-08; **game version:** RimWorld 1.6.4633 rev1246 on Windows;
**test branch:** `feature/authoritative-iteration10-chunked-checkpoints`; **source:** user-provided in-game log and screenshots.

## Confirmed by the first actual game session

- The isolated `Authoritative Server Probe (Experimental)` assembly loaded with Harmony and Core; the original Multiplayer mod was disabled.
- A one-map colony could be opened and the custom mod settings UI displayed.
- The game initially refused start while **vanilla selected speed** was not `TimeSpeed.Paused`. Opening a menu's *effective* pause is not equivalent to selecting the paused speed.
- After manually clicking pause, the Unity simulation hook initialized and the loopback TCP listener opened at a dynamic port. The log contained:
  - `[Authoritative] Single-map simulation loop activated on the Unity thread`
  - `[Authoritative Probe] local-only server running on 127.0.0.1:54143`
- The host immediately stopped while running the first tick, logging `[Authoritative] Stopping simulation after host error: System.InvalidOperationException: Pause vanilla time before invoking the authoritative driver`, through `RimWorldSingleMapDriver.EnsureReady -> ExecuteGameTick -> SimulationDomainManager.Advance -> AuthoritativeSimulationHost.Pump -> SingleMapUnityTickLoop.RunFrame`.
- Consequently, client connections to `127.0.0.1:54143` failed with `SocketException: connection refused`. Windows `Test-NetConnection` returned `TcpTestSucceeded: False`, and `Get-NetTCPConnection -LocalPort 54143` returned no listener.
- The settings UI continued showing `Running` despite the listener being closed, because its instance-local `host` field was not cleared by the hook's emergency stop.

**This is authentic runtime evidence of loading and a successful initial server bind only.** It does not show a completed gameplay tick, connected client, movement, map rendering, saves or real multiplayer.

## Fix in the following commit — requires user retest

- While `SingleMapUnityTickLoop` is active, the Harmony `TickManager.TickManagerUpdate` prefix suppresses the original vanilla tick path. On each Unity update **before executing the authoritative Pump**, the hook now explicitly restores `Find.TickManager.CurTimeSpeed = TimeSpeed.Paused` when a menu closes or a speed button changes it. The selected vanilla speed must remain paused while the server is sole tick owner.
- The initial activation guard still requires the user to **select the pause speed on the colony map**, rather than relying on an automatically paused menu. Mod settings explain this.
- On emergency shutdown, the hook attempts to restore selected vanilla pause **before** clearing its active hook; this prevents the normal RimWorld tick path from resuming unexpectedly under a non-paused speed after a fault. Any failure to pause is separately logged.
- When settings reopen after emergency shutdown, they clear the stale host/token/port and show a diagnostic message rather than advertising a dead TCP listener.

**The fix is source-only until retested in a licensed running RimWorld.** CI compilation cannot prove the correction works at runtime.

## Retest recipe

1. Stop the old probe if shown; exit RimWorld without saving the disposable colony and update the local git branch to the fix commit.
2. Rebuild the probe and console client; stage with the allowlist packaging script; replace the installed test mod folder while the game is closed.
3. Start a fresh single-map test colony. Select the actual vanilla **Paused** time speed by mouse before entering mod settings.
4. Click Start. Copy the one-time credential. Close settings and wait briefly; reopen settings and verify it still shows the active session and the new port.
5. From PowerShell run `Get-NetTCPConnection -LocalPort PORT` and `Test-NetConnection 127.0.0.1 -Port PORT`; verify a listener exists.
6. Run the console probe client with the **new** port and token. Check its snapshot and then issue one validated pawn Goto move, observing server and client positions.
7. If any step fails, capture the `[Authoritative]` / `[Authoritative Probe]` log lines and stack trace. Never port-forward the loopback listener.

The previous `54143` was a per-run ephemeral port and is **not expected to survive a restart**.
