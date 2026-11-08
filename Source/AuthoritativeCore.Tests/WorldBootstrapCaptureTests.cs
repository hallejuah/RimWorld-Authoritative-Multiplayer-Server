using Multiplayer.Authoritative;

internal static class WorldBootstrapCaptureTests
{
    private static readonly ContentManifest Manifest = new(
        "1.6.4566",
        new[] { new ModFingerprint("ludeon.rimworld", "1.6", new string('a', 64)) },
        new string('b', 64));

    private static WorldBootstrapV2 Build(params IWorldBootstrapMapSource[] sources) =>
        WorldBootstrapCapture.Capture(Guid.NewGuid(), 12, 105, Manifest, sources);

    public static void Layers()
    {
        var source = new FakeMapSource(1);
        var result = Build(source);
        var map = result.Maps[0];
        Verify(map.Width == 3 && map.Height == 2 && map.WorldTile == 42, "map dimensions");
        Verify(map.TerrainDefs.SequenceEqual(new[] { "Soil", "Sand" }), "terrain definitions");
        Verify(map.Terrain.SequenceEqual(new byte[] { 0, 1, 0, 1, 0, 1 }), "row-major terrain");
        Verify(map.RoofDefs.SequenceEqual(new[] { "RoofRockThin" }), "roof definitions");
        Verify(map.Roof.SequenceEqual(new byte[] { 1, 0, 0, 1, 0, 0 }), "roof index 0 vs 1");
        Verify(map.FogBits.SequenceEqual(new byte[] { 0b0010_0001 }), "packed LSB fog");
        Verify(result.Entities.Count == 1 && result.Entities[0].Kind == PresentationKind.Pawn &&
            result.Entities[0].Pawn?.CurrentJobDef == "Goto", "real source entities");
        Verify(result.NetworkVersion == 12 && result.GameStateVersion == 105,
            "server-assigned revisions");
        Verify(source.ReadCount == 1, "entity scan occurred more than once");
        var roundtrip = WorldBootstrapCodec.Decode(WorldBootstrapCodec.Encode(result));
        Verify(roundtrip.Entities[0].Id == 100, "wire did not preserve data");
    }

    public static void Ordering()
    {
        var one = new FakeMapSource(1);
        var two = new FakeMapSource(2) { Entities = new[] {
            new EntityPresentation(300, 2, 0, "Granite", 2, 0, 1, 10,
                PresentationKind.Thing, null)
        }};
        var world = Guid.NewGuid();
        var front = WorldBootstrapCapture.Capture(world, 1, 2, Manifest,
            new IWorldBootstrapMapSource[] { one, two });
        var back = WorldBootstrapCapture.Capture(world, 1, 2, Manifest,
            new IWorldBootstrapMapSource[] { two, one });
        Verify(front.Maps[0].DomainId == 1 && front.Maps[1].DomainId == 2,
            "map domains not sorted");
        Verify(WorldBootstrapCodec.ComputeSha256(front) ==
            WorldBootstrapCodec.ComputeSha256(back),
            "same source data emitted inconsistent canonical bootstrap");
    }

    public static void Rejection()
    {
        var invalidSource = new FakeMapSource(1) { Width = 0 };
        Throws<ArgumentException>(() => Build(invalidSource));
        var duplicate = new FakeMapSource(1);
        Throws<ArgumentException>(() => Build(duplicate, duplicate));
        var outOfBounds = new FakeMapSource(1) {
            Entities = new[] { new EntityPresentation(100, 1, 0, "Human", 20, 0, 0, 1,
                PresentationKind.Pawn, new PawnPresentation(false, false, false, null)) }
        };
        Throws<ArgumentException>(() => Build(outOfBounds));
        var largePalette = new FakeMapSource(1) {
            Width = 257, Height = 1, Terrain = (x, _) => "Tile" + x
        };
        Throws<InvalidOperationException>(() => Build(largePalette));
        var duplicateIds = new FakeMapSource(2) {
            Entities = new[] { new EntityPresentation(100, 2, 0, "WoodLog", 0, 0, 0, 1,
                PresentationKind.Thing, null) }
        };
        Throws<ArgumentException>(() => Build(new FakeMapSource(1), duplicateIds));
        var missingTerrain = new FakeMapSource(1) { Terrain = (_, _) => "" };
        Throws<InvalidOperationException>(() => Build(missingTerrain));
    }

    public static void ManifestAdmission()
    {
        var source = new FakeMapSource(1);
        var wrong = Manifest with { DefCatalogSha256 = "placeholder" };
        Throws<ArgumentException>(() =>
            WorldBootstrapCapture.Capture(Guid.NewGuid(), 0, 0, wrong,
                new IWorldBootstrapMapSource[] { source }));
        Verify(source.CellReadCount == 0 && source.ReadCount == 0,
            "invalid content manifest still triggered real map reads");
        Throws<ArgumentException>(() =>
            WorldBootstrapCapture.Capture(Guid.Empty, 0, 0, Manifest,
                new IWorldBootstrapMapSource[] { source }));
        Verify(source.CellReadCount == 0, "invalid world still read game state");
    }

    private static void Verify(bool ok, string reason)
    {
        if (!ok) throw new Exception(reason);
    }

    private static void Throws<T>(Action run) where T : Exception
    {
        try { run(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class FakeMapSource : IWorldBootstrapMapSource
    {
        public int DomainId { get; }
        public int WorldTile => 42;
        public int Width { get; set; } = 3;
        public int Height { get; set; } = 2;
        public Func<int, int, string> Terrain { get; set; } =
            (x, z) => (x + z) % 2 == 0 ? "Soil" : "Sand";
        public Func<int, int, string?> Roof { get; set; } =
            (x, z) => x == 0 ? "RoofRockThin" : null;
        public Func<int, int, bool> Fog { get; set; } =
            (x, z) => (x == 0 && z == 0) || (x == 2 && z == 1);
        public IReadOnlyList<EntityPresentation> Entities { get; set; }
        public int ReadCount { get; private set; }
        public int CellReadCount { get; private set; }

        public FakeMapSource(int domainId)
        {
            DomainId = domainId;
            Entities = new[] {
                new EntityPresentation(100, domainId, 2, "Human", 0, 0, 0, 1,
                    PresentationKind.Pawn,
                    new PawnPresentation(false, false, false, "Goto"))
            };
        }

        public string TerrainDefAt(int x, int z)
        {
            CellReadCount++;
            return Terrain(x, z);
        }
        public string? RoofDefAt(int x, int z) => Roof(x, z);
        public bool FoggedAt(int x, int z) => Fog(x, z);
        public IReadOnlyList<EntityPresentation> ReadSpawnedEntities()
        {
            ReadCount++;
            return Entities;
        }
    }
}
