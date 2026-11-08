using Multiplayer.Authoritative;
using Multiplayer.Authoritative.Transport;

internal static class Fixtures
{
    public static readonly Guid World = Guid.Parse("5c0f74b2-b85f-43f9-a22b-b77d2cefd619");
    // Synthetic fixture hashes; never use these for real game admission.
    public static ContentManifest Manifest => new("1.6.4566",
        [new("ludeon.rimworld", "1.6", new string('a', 64)), new("test.fixture", "1", new string('b', 64))],
        new string('c', 64));

    public static WorldViewCheckpoint View(long version = 10, long gameVersion = 20)
    {
        EntityPresentation pawn = new(1, 10, 1, "Human", 1, 1, 0, 1, PresentationKind.Pawn, new(false, false, false, "Wait"));
        EntityPresentation item = new(2, 10, 0, "Steel", 3, 3, 0, 25, PresentationKind.Thing, null);
        var nodes = new List<EntityGraphNode> { new(pawn, new(0, EntityHolderSlot.None)), new(item, new(0, EntityHolderSlot.None)) };
        for (var id = 3; id <= 202; id++)
            nodes.Add(new(new(id, 10, 0, "Steel", -1, -1, 0, 50, PresentationKind.Thing, null), new(1, EntityHolderSlot.Inventory)));
        var map = new MapLayerSnapshot(10, 7, 96, 96, ["Soil"], new byte[96 * 96], [], new byte[96 * 96], new byte[96 * 96 / 8]);
        return new(new(WorldBootstrapFormat.Version, World, version, gameVersion, Manifest, [map], [pawn, item]),
            new(EntityGraphFormat.Version, World, version, gameVersion, [new(10, 96, 96)], nodes, [new(1, EntityHolderSlot.Inventory)]));
    }

    public static PreparedWorldViewTransfer Plan() => new(View(), WorldViewTransferFormat.MinChunkBytes);
    public static WorldViewTransferAssembler Assembler() => new(Manifest, World);
    public static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    public static void Feed(PreparedWorldViewTransfer plan, WorldViewTransferAssembler receiver)
    {
        for (var i = 0; i < plan.ChunkCount; i++)
            Check(receiver.Accept(plan.GetChunk(i)) is WorldViewChunkResult.Accepted or WorldViewChunkResult.Duplicate, "feed rejected");
    }
    public static void Same(WorldViewCheckpoint expected, WorldViewCheckpoint actual)
    {
        Check(WorldBootstrapCodec.Encode(expected.Bootstrap).SequenceEqual(WorldBootstrapCodec.Encode(actual.Bootstrap)), "map mismatch");
        Check(EntityGraphCodec.Encode(expected.Graph).SequenceEqual(EntityGraphCodec.Encode(actual.Graph)), "graph mismatch");
    }

    // Builds deliberately inconsistent envelopes to test the receiver boundary;
    // PreparedWorldViewTransfer correctly refuses such pairs at the source.
    public static void FeedRaw(WorldViewTransferAssembler receiver, WorldBootstrapV2 bootstrap, EntityGraphSnapshot graph,
        Func<WorldViewOffer, WorldViewOffer>? edit = null)
    {
        var a = WorldBootstrapCodec.Encode(bootstrap);
        var b = EntityGraphCodec.Encode(graph);
        var payload = a.Concat(b).ToArray();
        var offer = new WorldViewOffer(WorldViewTransferFormat.Version, WorldBootstrapFormat.Version, EntityGraphFormat.Version,
            Guid.NewGuid(), World, bootstrap.NetworkVersion, bootstrap.GameStateVersion, a.Length, b.Length, 4096,
            WorldViewTransferValidation.ContentDigest(Manifest), WorldViewTransferValidation.Digest(payload));
        if (edit is not null) offer = edit(offer);
        Check(receiver.Begin(offer) == WorldViewOfferResult.Started, "raw fixture offer rejected");
        var count = WorldViewTransferValidation.ValidateOffer(offer);
        for (var i = 0; i < count; i++)
        {
            var bytes = payload.Skip(i * offer.ChunkBytes).Take(offer.ChunkBytes).ToArray();
            Check(receiver.Accept(new(offer.TransferId, i, bytes, WorldViewTransferValidation.Digest(bytes))) ==
                WorldViewChunkResult.Accepted, "raw fixture chunk rejected prematurely");
        }
    }
}
