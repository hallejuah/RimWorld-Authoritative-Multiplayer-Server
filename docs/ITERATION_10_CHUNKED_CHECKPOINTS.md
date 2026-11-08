# Iteration 10: chunked world-view transfer and reconnect recovery

## Scope and verified evidence

Built on iteration 9 (`77d9f9657123ac814659ef4967a68bc1d399064d`). The map bootstrap v2 and entity graph v1 can now be sent together through a separate bounded TCP checkpoint-transfer API. This is original code; no Amity implementation, protocol, binaries or assets are imported.

Implementation/test commit `ad69e8cf5e71513f23f57f15988afcae0188609b` passed GitHub Actions run **42503982290**. The job log reports **32/32 checkpoint transfer cases**, **47/47 core**, **22/22 graph**, and **7/7 existing transport**: **108/108 total**. The core/transport net8.0 and netstandard2.0 targets, net48 Verse bridge, optional probe mod, probe client and isolated packaging also passed. Compiler build summaries reported zero warnings/errors; the pre-existing workflow actions still emitted a Node runtime deprecation notice.

The new socket tests use real loopback TCP and synthetic DTO fixtures, NOT a running RimWorld process. No licensed RimWorld/Unity or net48/Mono runtime test was performed. CI compilation against game reference assemblies is not game-runtime acceptance. The final documentation commit must be checked separately in Actions.

## What is implemented

- `PreparedWorldViewTransfer`: validate an entire `WorldViewCheckpoint`, encode both parts once, concatenate them, assign a unique transfer ID, and produce independent bounded chunk copies. The prepared object is immutable and reusable by socket workers without touching Verse.
- `WorldViewTransferAssembler`: one bounded staging buffer; expected world/content and minimum revision admission; verified chunk collection; exact duplicate recognition; conflict rejection; missing-index queries; complete checksum and semantic validation; explicit buffer disposal.
- `WorldViewPacketCodec`: independently versioned typed binary framing with exact/type-specific size checks before allocation and full-read loops for fragmented streams.
- `LoopbackWorldViewTransfer`: a read-only request/reply exchange on an already-connected loopback `TcpClient`, with a one-chunk request window, bounded request work, overall deadline, cancellation and final acknowledgement.
- `Source/AuthoritativeCheckpointTransfer.Tests`: 32 additional automated cases and a CI step. Existing v1 TCP/lockstep protocol, map v2 schema and graph v1 schema are unchanged.

This is NOT automatically wired into the existing probe mod/client UI. It adds no game listener, login screen, renderer, state deltas or gameplay command. The next integration must capture a real view on the Unity thread, publish an immutable prepared transfer, and hand the received candidate to the presentation thread.

## Exchange and integrity

```text
client -> Hello(transfer protocol version, expected world UUID, local content digest)
server -> Offer(transfer UUID, world UUID, both schema versions, revisions,
                component lengths, chunk size, content digest, whole-payload digest)
client -> GetChunk(transfer UUID, missing index)
server -> Chunk(transfer UUID, index, per-chunk digest, payload)
          ... repeat with at most one outstanding request ...
client -> Finish(transfer UUID, whole-payload digest)
server -> Complete(transfer UUID)
```

The sender checks the hello before sending any offer/world payload. The receiver checks the offer before allocating the staging buffer. Content digest calculation validates and canonicalizes the existing manifest, preserves mod order and hashes length-prefixed UTF-8 fields. It inherits the manifest's existing limits: resolved Def identities are not full semantic Def/settings validation.

A chunk must match the active transfer, expected index range, exact position-dependent length and SHA-256. A duplicate with identical bytes is harmless. A duplicate with different bytes does not overwrite accepted data. Once all chunks are present, the receiver verifies the whole digest, decodes both existing formats, checks offer metadata against decoded metadata, and invokes the complete map/graph consistency validator.

`Finish` acknowledges a validated candidate, not a gameplay operation or a durable save. `ReceiveAsync` returns only after `Complete`. It does not install the candidate itself. Use `ClientWorldViewReplica.TryInstall` on its creating presentation thread; that existing API provides atomic replacement and revision conflict protection. Partial or failed transfers never mutate that replica.

SHA-256 detects corruption; it is NOT sender authentication, encryption, an anti-cheat guarantee or a signature. No client-uploaded save or client-side gameplay authority is introduced.

## Resume, replacement and cleanup

Keep the same `PreparedWorldViewTransfer` on the server and `WorldViewTransferAssembler` on the client across a dropped socket. Establish a new loopback connection and call the APIs again. An identical offer reuses already-verified chunks, so only missing indices are requested. No automatic retry/backoff loop is hidden in this API.

If the final acknowledgement was lost after all chunks arrived, the same offer permits revalidation and a fresh Finish/Complete exchange without retransmitting the payload. If the server instead supplies a newer checkpoint, staging starts clean and late chunks from the previous transfer are rejected.

Reusing a transfer UUID with changed metadata is a conflict. Rewriting a network revision with different content is also a conflict. A different transfer UUID may re-chunk identical checkpoint bytes, but this starts a new buffer rather than reusing chunks under incompatible boundaries. Older network or game revisions are rejected.

A complete payload that fails checksum or semantic validation is discarded; its offer/revision guard remains. A corrupt offer must not be silently trusted under a new meaning. Correcting a conflicting advertised checkpoint requires a genuinely new server revision or an explicitly trusted new assembler/session policy, not automatic weakening of revision checks. Restarting a process loses in-memory transfer progress. Durable resume/world epochs and save rollback policy remain future work.

`DiscardBuffers()` explicitly releases staging after abandonment, timeout policy, or successful application. It retains the last offer's revision/content guard, so it does not enable rollback. An identical offer can subsequently allocate a fresh buffer. The library does not run a background eviction timer or own a global session cache.

## Bounds, ownership and threading

Each existing component is limited to 8 MiB; combined encoded payload is at most **16 MiB**. Chunk sizes are **4 KiB to 64 KiB**, default **32 KiB**; at most **4,096 chunks**. A missing-index query returns at most 64 indices, while the TCP path requests one at a time. A sender permits at most `chunkCount + 8` chunk replies per connection, preventing unlimited duplicate-request work.

These are encoded-buffer limits, NOT a total process-memory guarantee. Preparing, framing, decoding and validating make additional bounded copies and allocate DTO collections. Replacing a partial transfer allocates the new buffer before releasing the old one. Callers must bound the number of retained prepared snapshots, assemblers and concurrent connections; this helper deliberately creates no listener or server-wide cache.

Assembler operations are individually locked, allowing serialized async continuations and concurrent chunk callbacks without corrupting counters. **Only one logical receiving socket session may use an assembler at a time.** Do not concurrently call Begin/Discard/Receive from competing sessions. A prepared transfer is immutable and may be shared between senders. Returned checkpoints are independent copies.

Socket methods take ownership of their supplied `TcpClient` and close it on success, failure or cancellation. A finite overall timeout is mandatory: positive and at most five minutes. Cancellation closes the socket to interrupt pending I/O rather than assuming `NetworkStream.ReadTimeout` controls `ReadAsync`. This behavior was tested on net8.0 Linux; verify the actual target Mono/Unity runtime separately.

## Wire layout

Frame header: ASCII `RWVT`, big-endian u32 length INCLUDING the kind byte, then kind:u8. Body integers are little endian; UUIDs use `Guid.ToByteArray()` representation; digests are 32 raw bytes. There is no compression or arbitrary string field in the frame.

| Kind | Body | Bytes |
| --- | --- | --- |
| 1 Hello | version:i32, world:16, content SHA256:32 | 52 |
| 2 Offer | transfer/bootstrap/graph versions:i32 each; transfer:16; world:16; network/game:i64 each; bootstrap/graph/chunk lengths:i32 each; content/payload SHA256:32 each | 136 |
| 3 GetChunk | transfer:16, index:i32 | 20 |
| 4 Chunk | transfer:16, index:i32, chunk SHA256:32, data | 53 through 65,588 |
| 5 Finish | transfer:16, payload SHA256:32 | 48 |
| 6 Complete | transfer:16 | 16 |

Unknown kinds/versions, inconsistent lengths, invalid scalar values and EOF within a header/body fail closed. Clean EOF is recognized only before a new header. Old v1 frames cannot be mistaken for this magic-prefixed protocol. The total maximum frame size including header is 65,597 bytes.

## Intended integration sequence (not an implemented UI)

1. On the trusted paused single-map Unity thread, obtain the real content manifest and call `VerseWorldViewCapture.Capture(driver, worldTile, manifest, networkVersion)`. Finish the outstanding iteration-8/9 runtime acceptance first.
2. Prepare a `PreparedWorldViewTransfer` from that consistent DTO snapshot. Retain this object for the desired resume lifetime. Socket workers must not capture Verse state themselves.
3. Supply an already-connected **loopback-only** server socket to `LoopbackWorldViewTransfer.SendAsync(peer, prepared, timeout, token)`.
4. On the client, create a `WorldViewTransferAssembler` with the expected server world, actual local manifest and appropriate revision floors. Call `ReceiveAsync(peer, assembler, timeout, token)` on its one logical receiving session. On a recoverable disconnect, reconnect with the same assembler; on abandonment, discard buffers.
5. Queue the returned DTO candidate onto the presentation thread and call its `ClientWorldViewReplica.TryInstall`. Only a successful result may replace what the future renderer displays.

The existing probe host and standalone probe client continue using v1 until a later integration change. Do not claim these APIs already deliver a live colony to a player UI.

## Security and production boundaries

Both endpoints are checked with `IPAddress.IsLoopback`; unconnected or remote sockets are rejected. There are no credentials or TLS in this separate experimental path. Loopback is a deployment restriction, not authentication of other local processes. Do not port-forward, proxy or expose it remotely. The full snapshot may contain private colony/fog/inventory data: authenticated sessions and server-side interest filtering are prerequisites for remote use.

This iteration does not add full semantic mod admission, authenticated world/epoch negotiation, automatic reconnect backoff, per-user/global rate limiting, compressed checkpoints, multi-chunk sliding windows, graph/map deltas, a renderer, deep pawn state, durable save/journal recovery, or cross-domain gameplay. No performance or production-readiness claim is made.

## Reproduce automated checks

From the repository root with the project's .NET SDK and package prerequisites:

```sh
dotnet build Source/AuthoritativeTransport/AuthoritativeTransport.csproj -c Release --warnaserror
dotnet run --project Source/AuthoritativeCheckpointTransfer.Tests/AuthoritativeCheckpointTransfer.Tests.csproj -c Release
dotnet run --project Source/AuthoritativeCore.Tests/AuthoritativeCore.Tests.csproj -c Release
dotnet run --project Source/AuthoritativeEntityGraph.Tests/AuthoritativeEntityGraph.Tests.csproj -c Release
dotnet run --project Source/AuthoritativeTransport.Tests/AuthoritativeTransport.Tests.csproj -c Release
```

New tests cover four chunk sizes, 20 reorder/duplicate seeds, incomplete staging, chunk and offer bounds, foreign identities, revision floors/conflicts, replacement/discard, whole and per-chunk integrity, semantically inconsistent but checksummed pairs, metadata/content mismatch, defensive copies, concurrent callbacks, all packet variants, one-byte/coalesced reads, every truncation position, malformed headers/scalars, real TCP roundtrip/reconnect/lost acknowledgement/newer checkpoint, corruption recovery, admission before world data, invalid/excess requests, deadlines, cancellation and endpoint guards.
