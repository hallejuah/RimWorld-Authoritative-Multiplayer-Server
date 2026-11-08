var cases = new (string Name, Func<Task> Run)[]
{
    ("Checkpoint chunks roundtrip at all supported test sizes", () => Sync(TransferTests.Roundtrip)),
    ("Reordered and duplicated chunks reconstruct canonically", () => Sync(TransferTests.OrderingAndDuplicates)),
    ("Incomplete staging never installs partial client state", () => Sync(TransferTests.Incomplete)),
    ("Corrupt and conflicting duplicate chunks preserve accepted bytes", () => Sync(TransferTests.CorruptionAndConflict)),
    ("Chunk identities, indexes, lengths and pull windows are bounded", () => Sync(TransferTests.ChunkBounds)),
    ("Malformed offers cannot replace valid staging buffers", () => Sync(TransferTests.OfferBounds)),
    ("World, content and revision admission precede buffer allocation", () => Sync(TransferTests.Admission)),
    ("Identical offers resume but revision rewrites fail", () => Sync(TransferTests.ResumeAndRevisionGuards)),
    ("Newer checkpoints replace partial transfers without mixing chunks", () => Sync(TransferTests.Replacement)),
    ("Explicit discard releases buffers without losing revision guards", () => Sync(TransferTests.Discard)),
    ("Whole-payload checksum is checked after chunk checksums", () => Sync(TransferTests.WholeDigest)),
    ("Checksummed but inconsistent map and graph pairs are rejected", () => Sync(TransferTests.PairMismatch)),
    ("Decoded world and revision must match the advertised offer", () => Sync(TransferTests.MetadataMismatch)),
    ("Decoded manifest must match canonical ordered admission digest", () => Sync(TransferTests.ManifestMismatch)),
    ("Prepared, assembled and returned checkpoint buffers are isolated", () => Sync(TransferTests.DefensiveCopies)),
    ("Concurrent chunk callbacks cannot double-count or corrupt staging", () => Sync(TransferTests.ConcurrentChunks)),
    ("All typed wire packets roundtrip", PacketTests.Roundtrip),
    ("One-byte reads and coalesced packets preserve framing", PacketTests.Fragmentation),
    ("EOF inside every packet byte position fails", PacketTests.Truncation),
    ("Frame magic, types and oversize lengths fail before body reads", PacketTests.BadHeaders),
    ("Wire scalar validation and cancellation are enforced", PacketTests.BadScalarsAndCancellation),
    ("Real TCP transfers a map and graph into an installable view", SocketTests.Roundtrip),
    ("Real TCP reconnect requests only missing verified chunks", SocketTests.Reconnect),
    ("Lost final acknowledgement resumes without resending payload", SocketTests.LostCompletion),
    ("Reconnect to a newer checkpoint starts a clean transfer", SocketTests.NewCheckpointAfterReconnect),
    ("TCP world/content mismatch sends no world offer", SocketTests.AdmissionBeforeData),
    ("Corrupt TCP chunk fails and a clean reconnect recovers", SocketTests.CorruptChunk),
    ("TCP rejects foreign, out-of-range and false completion requests", SocketTests.InvalidRequests),
    ("TCP duplicate requests cannot exceed the server work budget", SocketTests.RequestBudget),
    ("Idle TCP sender and receiver respect finite deadlines", SocketTests.Deadlines),
    ("Cancellation preserves resumable progress until explicit discard", SocketTests.CancellationPreservesProgress),
    ("Endpoint and timeout guards reject unsupported sessions", SocketTests.EndpointAndTimeoutGuards)
};
var failures = 0;
foreach (var test in cases)
{
    try
    {
        await test.Run().WaitAsync(TimeSpan.FromSeconds(30));
        Console.WriteLine("PASS: " + test.Name);
    }
    catch (Exception e)
    {
        failures++;
        Console.Error.WriteLine("FAIL: " + test.Name + ": " + e);
    }
}
Console.WriteLine($"{cases.Length - failures}/{cases.Length} checkpoint transfer tests passed");
return failures == 0 ? 0 : 1;

static Task Sync(Action test) { test(); return Task.CompletedTask; }
