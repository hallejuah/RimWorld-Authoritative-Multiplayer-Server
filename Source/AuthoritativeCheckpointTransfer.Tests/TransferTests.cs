using Multiplayer.Authoritative;
using Multiplayer.Authoritative.Transport;
using static Fixtures;

internal static class TransferTests
{
    public static void Roundtrip()
    {
        foreach (var size in new[] { 4096, 8192, 32768, 65536 })
        {
            var p = new PreparedWorldViewTransfer(View(), size);
            var r = Assembler();
            Check(r.Begin(p.Offer) == WorldViewOfferResult.Started, "offer");
            Check(p.ChunkCount == (p.Offer.BootstrapBytes + p.Offer.GraphBytes + size - 1) / size, "chunk count");
            var last = p.GetChunk(p.ChunkCount - 1);
            Check(last.Payload.Length == p.Offer.BootstrapBytes + p.Offer.GraphBytes - (p.ChunkCount - 1) * size, "last chunk length");
            for (var i = p.ChunkCount - 1; i >= 0; i--) Check(r.Accept(p.GetChunk(i)) == WorldViewChunkResult.Accepted, "reordered chunk");
            Same(View(), r.GetValidatedCheckpoint());
            Check(r.GetMissingIndices().Length == 0, "complete transfer has missing parts");
        }
    }

    public static void OrderingAndDuplicates()
    {
        var p = Plan();
        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            var r = Assembler(); r.Begin(p.Offer);
            var order = Enumerable.Range(0, p.ChunkCount).OrderBy(_ => random.Next()).ToArray();
            foreach (var index in order)
            {
                Check(r.Accept(p.GetChunk(index)) == WorldViewChunkResult.Accepted, "first chunk");
                Check(r.Accept(p.GetChunk(index)) == WorldViewChunkResult.Duplicate, "duplicate counted twice");
            }
            Check(r.ReceivedCount == p.ChunkCount, "received count");
            Same(View(), r.GetValidatedCheckpoint());
        }
    }

    public static void Incomplete()
    {
        var p = Plan(); var r = Assembler(); r.Begin(p.Offer);
        r.Accept(p.GetChunk(0));
        Throws<InvalidOperationException>(() => r.GetValidatedCheckpoint());
        Check(r.ReceivedCount == 1 && r.GetMissingIndices(1).Single() == 1, "incomplete read lost progress");
        var client = new ClientWorldViewReplica(Manifest, World);
        client.TryInstall(View(9, 19));
        Check(client.NetworkVersion == 9, "partial reception mutated installed state");
        Feed(p, r);
        Check(client.TryInstall(r.GetValidatedCheckpoint()) == WorldViewInstallResult.Accepted && client.NetworkVersion == 10,
            "complete candidate not committed");
    }

    public static void CorruptionAndConflict()
    {
        var p = Plan(); var r = Assembler(); r.Begin(p.Offer);
        var bad = p.GetChunk(0); bad.Payload[0] ^= 1;
        Check(r.Accept(bad) == WorldViewChunkResult.InvalidChunk && r.ReceivedCount == 0, "corrupt chunk stored");
        Check(r.Accept(p.GetChunk(0)) == WorldViewChunkResult.Accepted, "valid retry rejected");
        bad = bad with { Sha256 = WorldViewTransferValidation.Digest(bad.Payload) };
        Check(r.Accept(bad) == WorldViewChunkResult.Conflict && r.ReceivedCount == 1, "conflicting duplicate overwrote data");
        Feed(p, r); Same(View(), r.GetValidatedCheckpoint());
    }

    public static void ChunkBounds()
    {
        var p = Plan(); var r = Assembler();
        Check(r.Accept(p.GetChunk(0)) == WorldViewChunkResult.NoTransfer, "unsolicited chunk accepted");
        r.Begin(p.Offer);
        var chunk = p.GetChunk(0);
        Check(r.Accept(chunk with { TransferId = Guid.NewGuid() }) == WorldViewChunkResult.ForeignTransfer, "foreign chunk");
        var invalid = new[] { chunk with { Index = -1 }, chunk with { Index = p.ChunkCount }, chunk with { Payload = null! },
            chunk with { Payload = [] }, chunk with { Payload = new byte[4095] }, chunk with { Payload = new byte[65537] },
            chunk with { Sha256 = "x" } };
        foreach (var item in invalid) Check(r.Accept(item) == WorldViewChunkResult.InvalidChunk, "invalid chunk accepted");
        Check(r.Accept(null!) == WorldViewChunkResult.InvalidChunk && r.ReceivedCount == 0, "null chunk/progress");
        Throws<ArgumentOutOfRangeException>(() => p.GetChunk(-1));
        Throws<ArgumentOutOfRangeException>(() => p.GetChunk(p.ChunkCount));
        Throws<ArgumentOutOfRangeException>(() => r.GetMissingIndices(0));
        Throws<ArgumentOutOfRangeException>(() => r.GetMissingIndices(65));
    }

    public static void OfferBounds()
    {
        var p = Plan(); var r = Assembler(); r.Begin(p.Offer); r.Accept(p.GetChunk(0));
        var o = p.Offer;
        WorldViewOffer[] invalid = [o with { Version = 99 }, o with { BootstrapVersion = 99 }, o with { GraphVersion = 99 },
            o with { TransferId = Guid.Empty }, o with { WorldId = Guid.Empty }, o with { NetworkVersion = -1 },
            o with { GameStateVersion = -1 }, o with { BootstrapBytes = 0 }, o with { BootstrapBytes = int.MaxValue },
            o with { GraphBytes = 95 }, o with { GraphBytes = int.MaxValue }, o with { ChunkBytes = 4095 },
            o with { ChunkBytes = int.MaxValue }, o with { ContentSha256 = new string('A', 64) }, o with { PayloadSha256 = "bad" }, null!];
        foreach (var bad in invalid) Check(r.Begin(bad) == WorldViewOfferResult.InvalidOffer, "bad offer accepted");
        Check(r.ReceivedCount == 1 && r.Offer == o, "bad offer destroyed valid partial transfer");
        Throws<ArgumentOutOfRangeException>(() => new PreparedWorldViewTransfer(View(), 1));
        Throws<ArgumentOutOfRangeException>(() => new PreparedWorldViewTransfer(View(), 65537));
        var max = o with { BootstrapBytes = 8 * 1024 * 1024, GraphBytes = 8 * 1024 * 1024, ChunkBytes = 4096 };
        Check(WorldViewTransferValidation.ValidateOffer(max) == WorldViewTransferFormat.MaxChunks, "maximum chunk arithmetic");
    }

    public static void Admission()
    {
        var o = Plan().Offer; var r = Assembler();
        Check(r.Begin(o with { WorldId = Guid.NewGuid() }) == WorldViewOfferResult.WrongWorld, "wrong world");
        Check(r.Begin(o with { ContentSha256 = new string('0', 64) }) == WorldViewOfferResult.IncompatibleContent, "wrong manifest");
        Check(r.BufferedBytes == 0, "admission allocated payload buffer");
        var floor = new WorldViewTransferAssembler(Manifest, World, 10, 20);
        Check(floor.Begin(o with { NetworkVersion = 9 }) == WorldViewOfferResult.StaleRevision, "network floor");
        Check(floor.Begin(o with { GameStateVersion = 19 }) == WorldViewOfferResult.StaleRevision, "game floor");
        Throws<ArgumentException>(() => new WorldViewTransferAssembler(Manifest, Guid.Empty));
        Throws<ArgumentException>(() => new WorldViewTransferAssembler(Manifest, World, -1));
    }

    public static void ResumeAndRevisionGuards()
    {
        var p = Plan(); var r = Assembler(); r.Begin(p.Offer); r.Accept(p.GetChunk(0));
        Check(r.Begin(p.Offer) == WorldViewOfferResult.Resumed && r.ReceivedCount == 1, "resume lost verified chunk");
        Check(r.Begin(p.Offer with { NetworkVersion = 11 }) == WorldViewOfferResult.Conflict, "transfer ID changed meaning");
        Check(r.Begin(p.Offer with { TransferId = Guid.NewGuid(), PayloadSha256 = new string('0', 64) }) == WorldViewOfferResult.Conflict,
            "same revision changed payload");
        Check(r.Begin(p.Offer with { TransferId = Guid.NewGuid(), GameStateVersion = 21 }) == WorldViewOfferResult.Conflict, "same revision changed game state");
        Check(r.Begin(p.Offer with { TransferId = Guid.NewGuid(), NetworkVersion = 9 }) == WorldViewOfferResult.StaleRevision, "rollback");
        Check(r.ReceivedCount == 1, "rejection destroyed progress");
        Check(r.Begin(p.Offer with { TransferId = Guid.NewGuid(), ChunkBytes = 8192 }) == WorldViewOfferResult.Started && r.ReceivedCount == 0,
            "fresh rechunk did not reset staging");
    }

    public static void Replacement()
    {
        var old = Plan(); var newer = new PreparedWorldViewTransfer(View(11, 21), 4096);
        var r = Assembler(); r.Begin(old.Offer); r.Accept(old.GetChunk(0));
        Check(r.Begin(newer.Offer) == WorldViewOfferResult.Started && r.ReceivedCount == 0, "new checkpoint mixed with old buffer");
        Check(r.Accept(old.GetChunk(1)) == WorldViewChunkResult.ForeignTransfer, "late packet from old transfer");
        Feed(newer, r); Same(View(11, 21), r.GetValidatedCheckpoint());
    }

    public static void Discard()
    {
        var p = Plan(); var r = Assembler(); r.Begin(p.Offer); r.Accept(p.GetChunk(0));
        r.DiscardBuffers(); r.DiscardBuffers();
        Check(r.BufferedBytes == 0 && r.ReceivedCount == 0 && r.Offer == p.Offer, "discard lost guards or retained memory");
        Throws<InvalidOperationException>(() => r.GetMissingIndices());
        Check(r.Begin(p.Offer with { TransferId = Guid.NewGuid(), NetworkVersion = 9 }) == WorldViewOfferResult.StaleRevision, "discard enabled rollback");
        Check(r.Begin(p.Offer) == WorldViewOfferResult.Started, "could not restart identical transfer");
        Feed(p, r); Same(View(), r.GetValidatedCheckpoint());
    }

    public static void WholeDigest()
    {
        var p = Plan(); var r = Assembler(); r.Begin(p.Offer with { PayloadSha256 = new string('0', 64) });
        Feed(p, r);
        Throws<InvalidDataException>(() => r.GetValidatedCheckpoint());
        Check(r.BufferedBytes == 0 && r.ReceivedCount == 0, "failed whole digest retained poisoned buffer");
    }

    public static void PairMismatch()
    {
        var view = View(); var r = Assembler();
        var graph = view.Graph with { Nodes = view.Graph.Nodes.Select(n => n.Entity.Id == 2 ? n with { Entity = n.Entity with { X = 7 } } : n).ToArray() };
        FeedRaw(r, view.Bootstrap, graph);
        Throws<InvalidDataException>(() => r.GetValidatedCheckpoint());
        Check(r.BufferedBytes == 0, "inconsistent pair retained");
    }

    public static void MetadataMismatch()
    {
        var view = View(); var r = Assembler();
        FeedRaw(r, view.Bootstrap, view.Graph, o => o with { NetworkVersion = 11 });
        Throws<InvalidDataException>(() => r.GetValidatedCheckpoint());
        r = Assembler();
        FeedRaw(r, view.Bootstrap, view.Graph with { WorldId = Guid.NewGuid() });
        Throws<InvalidDataException>(() => r.GetValidatedCheckpoint());
    }

    public static void ManifestMismatch()
    {
        var view = View(); var r = Assembler();
        FeedRaw(r, view.Bootstrap with { Manifest = Manifest with { DefCatalogSha256 = new string('d', 64) } }, view.Graph);
        Throws<InvalidDataException>(() => r.GetValidatedCheckpoint());
        var digest = WorldViewTransferValidation.ContentDigest(Manifest);
        Check(digest != WorldViewTransferValidation.ContentDigest(Manifest with { Mods = Manifest.Mods.Reverse().ToArray() }), "mod order not hashed");
        Check(digest == WorldViewTransferValidation.ContentDigest(Manifest with
            { Mods = Manifest.Mods.Select(m => m with { PackageId = m.PackageId.ToUpperInvariant(), ContentSha256 = m.ContentSha256.ToUpperInvariant() }).ToArray() }),
            "manifest canonicalization differs");
    }

    public static void DefensiveCopies()
    {
        var input = View(); var p = new PreparedWorldViewTransfer(input, 4096); var r = Assembler(); r.Begin(p.Offer);
        input.Bootstrap.Maps[0].Terrain[0] = 240;
        var chunk = p.GetChunk(0); r.Accept(chunk); chunk.Payload[0] ^= 1;
        Check(p.GetChunk(0).Sha256 == WorldViewTransferValidation.Digest(p.GetChunk(0).Payload), "prepared payload leaked");
        Feed(p, r); var first = r.GetValidatedCheckpoint(); first.Bootstrap.Maps[0].Terrain[0] = 200;
        Same(View(), r.GetValidatedCheckpoint());
    }

    public static void ConcurrentChunks()
    {
        var p = Plan(); var r = Assembler(); r.Begin(p.Offer);
        Parallel.For(0, p.ChunkCount * 4, i =>
            Check(r.Accept(p.GetChunk(i % p.ChunkCount)) is WorldViewChunkResult.Accepted or WorldViewChunkResult.Duplicate, "concurrent acceptance"));
        Check(r.ReceivedCount == p.ChunkCount, "concurrent duplicate count");
        Same(View(), r.GetValidatedCheckpoint());
    }
}
