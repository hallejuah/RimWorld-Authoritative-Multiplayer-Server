# Experimental Authoritative Probe mod — build-only status

**Status:** compiled and staged by GitHub Actions, NOT installed or run in RimWorld.
This is an optional one-map developer-only local loopback experiment, not a dedicated server or a replacement for rwmt.Multiplayer.

## What was added

- `Source/AuthoritativeProbeMod` is a separately built net48 `Verse.Mod` entrypoint. Its mod settings expose **Start local authoritative probe**, **Copy one-time credential**, and **Stop authoritative probe**.
- `Experimental/AuthoritativeProbe/About/About.xml` is a standalone test-mod manifest; no original Multiplayer files are overwritten.
- `Source/AuthoritativeProbeClient` is a net8 command-line client for printing pawn positions and submitting one Goto movement.
- `scripts/package-authoritative-probe.sh` stages an isolated test mod, copying only an allowlist of compiled original/redistributable assemblies and explicitly excluding proprietary RimWorld/Unity/Verse references and Harmony runtime.

## Prepare a local staging folder (optional; do not run game testing yet)

From repository root, with .NET 8 SDK and a Bash shell:

```bash
dotnet build Source/AuthoritativeProbeMod/AuthoritativeProbeMod.csproj -c Release
dotnet build Source/AuthoritativeProbeClient/AuthoritativeProbeClient.csproj -c Release
bash scripts/package-authoritative-probe.sh
```

The last command creates `out/AuthoritativeProbeMod/About/About.xml` and `out/AuthoritativeProbeMod/Assemblies/*.dll`. The script does NOT copy proprietary game assemblies, content files, or HeadlessRim code. It copies only allowlisted third-party NuGet runtime dependencies that appear in `dotnet publish`; confirm third-party license notices and full Unity/Mono dependency compatibility before redistributing any staged artifacts. This staging is **not proof of a runnable mod**.

## Future manual game test (explicitly postponed)

1. Back up the RimWorld 1.6 test save. Make a separate mod profile with RimWorld Core and the separately installed Harmony mod. **Disable the original rwmt.Multiplayer mod entirely** in that profile. The probe refuses to start if the original Multiplayer assembly is loaded.
2. Copy the staged `AuthoritativeProbeMod` folder into RimWorld's Mods directory. The exact game path varies by installation. Enable *Authoritative Server Probe (Experimental)* and load one single-map colony, pausing vanilla time.
3. Open **Options → Mod settings → Authoritative Probe (local only)** and select **Start local authoritative probe**. The mod creates a new 256-bit session credential, explicitly starts the Unity tick loop and opens a random local TCP port on `127.0.0.1` only. The token is not logged or stored.
4. Note the displayed port. Click **Copy one-time credential**. The credential is available only for the current running probe. Never transmit it to another machine.
5. In a separate local terminal, run `dotnet run --project Source/AuthoritativeProbeClient -c Release -- PORT`, replacing PORT with the number displayed in settings. Paste the credential when prompted (hidden input). The client lists up to 50 spawned pawns as `ID, faction, domain, X, Z`.
6. To request one move, run `dotnet run --project Source/AuthoritativeProbeClient -c Release -- PORT move PAWN_ID X Z` and paste the credential. Use a valid spawned pawn ID and a free, walkable cell on its map. The client waits for a server receipt and polls the resulting network DTO state.
7. If receipt is accepted, observe the real pawn in the server-side game. The console should eventually confirm the destination position. The game must remain on one map with normal time control paused.
8. Press **Stop authoritative probe** in mod settings before returning to the main menu, switching saves or exiting. Test separately with a newly loaded game to verify cleanup and new token generation.

**CRITICAL:** Steps 1–8 above have **not** run in an actual RimWorld/Unity environment. Do not report them as passing until actual runtime logs and reproducible evidence exist.

## Limitations

- The local TCP connection uses cleartext credentials. It MUST NOT be forwarded, proxied, tunneled, or exposed on a LAN/WAN; the listener binds only to `IPAddress.Loopback`.
- Only one loaded map is supported, and vanilla game speed must remain paused. The host uses global `TickManager.DoSingleTick`, not independent multi-map RimWorld simulation.
- There is no full client RimWorld map/Thing renderer; the console receives read-only pawn DTOs. No real in-game multiplayer join or gameplay-tick suppression for remote game clients.
- The mod does not persist authentication, receipts, maps, full-game snapshots or save/reconnect state. It does not ship a production login server or TLS.
- Unity/Mono runtime dependency resolution for the staged NuGet assemblies must still be checked inside a real licensed RimWorld install.
- Some UI/game actions may mutate state outside the tick loop. This code is a controlled local experiment and must not be used as a production multiplayer server.
