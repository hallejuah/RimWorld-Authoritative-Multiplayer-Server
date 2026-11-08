# Local-only authoritative wire experiment

Status: compiled and socket-tested with a fake Unity driver. NOT a deployable multiplayer server.

## Scope and limitations

- Sources: Source/AuthoritativeTransport (net8.0 + netstandard2.0), Source/AuthoritativeTransport.Tests (net8.0 console).
- Address: TcpListener(IPAddress.Loopback, 0) only. A random local TCP port is assigned; this cannot bind a public address. NEVER tunnel or port-forward it to the internet or LAN.
- Security: credentials in the first Hello frame are sent in cleartext. The embedding application supplies ILoopbackAuthenticator.TryAuthenticate(credential, out trustedSession). This protocol is NOT encrypted and has no TLS or external account integration. Never use a test credential in production.
- State: initial snapshot and movement deltas contain ONLY minimal ThingState identifiers, faction/domain, X and Z. This is not enough to render/create a live RimWorld map and cannot replace RimWorld saves.

## Framing

Each frame starts with a four-byte big-endian length INCLUDING its one-byte kind. It then contains one byte of WireFrameKind and a bounded payload. All metadata payloads are typed JSON, never arbitrary CLR object graphs.

Message kinds:
- Hello (v1 protocol version plus credential, up to 1024 bytes): client -> server.
- Snapshot (up to 8 MiB): server -> client on authorized connection or delta-history miss.
- Command (up to 16 KiB): client -> server; validated against the authenticated connection session, faction, domain and game revision.
- Receipt (up to 4 KiB): server -> client after the Unity owner thread processes the command.
- Poll (up to 128 bytes): client -> server, asking for state changes after a network revision.
- Delta (up to 8 MiB each): server -> client, ordered, consecutive base/version, only retained revisions.
- EndPoll (empty): server -> client; terminates every successful polling response, including unchanged state.
- Error (up to 1024 bytes): server -> client for invalid protocol/operation.

Malformed kinds, oversized frames, truncated headers/bodies or incompatible protocol versions result in connection rejection; framing length is validated BEFORE allocating its payload. All session IDs inside PlayerCommand are untrusted and compared to the trusted server-side credential result.

## Thread boundary and reconnect

- Network tasks never touch Verse objects or call the game simulation.
- On command receipt, a server worker invokes AuthoritativeSimulationHost.EnqueueCommand(authenticatedSessionId, dto); the Unity owner thread calls Pump() and returns a CommandReceipt asynchronously.
- The owner thread also captures pawn DTOs through StatePublicationHub. Network tasks read a copied latest snapshot or retained deltas; missed revisions trigger a fresh snapshot.
- ClientStateReplica atomically applies snapshot and contiguous deltas; it never simulates jobs, pathfinding or the world.
- Reconnect starts with a full latest snapshot. There is no durable ledger or server restart recovery.
- Explicit stop closes the TCP listener and rejects pending command tasks, avoiding endless waits during normal host shutdown.

## Verification

Run:
```bash
dotnet build Source/AuthoritativeTransport/AuthoritativeTransport.csproj -c Release
dotnet run --project Source/AuthoritativeTransport.Tests/AuthoritativeTransport.Tests.csproj -c Release
```
Tests open real TCP sockets on 127.0.0.1 and use a fake game driver which only changes ThingState.X on the simulation owner thread. They cover the handshake, receipt, delta, reconnect, credential checks, ID mismatch, history gap and framing limits. GitHub Actions builds both net8 and netstandard2 code targets.

## Remaining work

TLS with authenticated sessions and account storage, replay/ordering protection across restarts, persistable IDs, map/pawn/job serialization, client game-object presentation, application-level acks, interest management and actual Linux/Windows RimWorld headless integration. Until then, this is a local technical experiment only.
