using Multiplayer.Authoritative;

internal static class WorldBootstrapTests
{
    private static readonly string HashA = new string('a', 64);
    private static readonly string HashB = new string('b', 64);
    private static readonly string HashC = new string('c', 64);

    public static void Roundtrip()
    {
        var sample = Create();
        var bytes = WorldBootstrapCodec.Encode(sample);
        var restored = WorldBootstrapCodec.Decode(bytes);

        Assert(restored.FormatVersion == 2 && restored.NetworkVersion == 10,
            "format revision or network revision changed");
        Assert(restored.Maps.Count == 2 && restored.Maps[0].DomainId == 1 &&
            restored.Maps[1].DomainId == 2, "map ordering not canonical");
        Assert(restored.Entities.Count == 2 && restored.Entities[0].Id == 100,
            "entity ordering not canonical");
        Assert(restored.Entities[0].Pawn?.CurrentJobDef == "Goto", "pawn view lost");
        Assert(restored.Maps[0].Terrain.SequenceEqual(new byte[] { 0, 1, 1, 0 }),
            "terrain packing changed");
        Assert(WorldBootstrapCodec.ComputeSha256(sample) ==
            WorldBootstrapCodec.ComputeSha256(restored), "canonical digest mismatch");
    }

    public static void InvalidMap()
    {
        // Map validation must fail on the layer itself, not just on a dangling
        // entity reference created when replacing the map collection.
        var sample = Create() with { Entities = Array.Empty<EntityPresentation>() };
        var map = sample.Maps[1];
        Bad(sample with { Maps = new[] { map with { Terrain = new byte[3] } } });
        Bad(sample with { Maps = new[] { map with { Terrain = new byte[] { 0, 2, 0, 0 } } } });
        Bad(sample with { Maps = new[] { map with { Roof = new byte[] { 0, 2, 0, 0 } } } });
        Bad(sample with { Maps = new[] { map with { FogBits = new byte[] { 128 } } } });
        Bad(sample with { Maps = new[] { map, map } });
        Bad(sample with { Maps = new[] { map with { Width = 513 } } });
        Bad(sample with { Maps = Array.Empty<MapLayerSnapshot>() });
        Bad(sample with { Maps = new[] { map with { TerrainDefs = new[] { "Soil", "Soil" } } } });
    }

    public static void InvalidEntity()
    {
        var sample = Create();
        var pawn = sample.Entities[1];
        Bad(sample with { Entities = new[] { pawn, pawn } });
        Bad(sample with { Entities = new[] { pawn with { DomainId = 999 } } });
        Bad(sample with { Entities = new[] { pawn with { X = 100 } } });
        Bad(sample with { Entities = new[] { pawn with { FactionId = -1 } } });
        Bad(sample with { Entities = new[] { pawn with { Rotation = 4 } } });
        Bad(sample with { Entities = new[] { pawn with { Pawn = null } } });
        Bad(sample with { Entities = new[] { pawn with { StackCount = 2 } } });
        Bad(sample with { Entities = new[] { pawn with { Pawn = new PawnPresentation(false, false, false, "invalid job") } } });
        // Neutral world things are represented by faction 0, not by a bogus player faction.
        var neutral = sample.Entities[0] with { FactionId = 0 };
        WorldBootstrapValidator.ValidateAndCopy(sample with { Entities = new[] { neutral } });
    }

    public static void ManifestMismatch()
    {
        var sample = Create();
        var local = sample.Manifest;
        Assert(ClientWorldBootstrapReplica.Compatible(local, local), "identical manifest rejected");
        var order = local with { Mods = local.Mods.Reverse().ToArray() };
        Assert(!ClientWorldBootstrapReplica.Compatible(local, order), "mod load order ignored");
        var hashes = local with { DefCatalogSha256 = HashB };
        Assert(!ClientWorldBootstrapReplica.Compatible(local, hashes), "Def catalog mismatch accepted");
        var changed = local with { Mods = new[] { local.Mods[0], local.Mods[1] with { ContentSha256 = HashC } } };
        Assert(!ClientWorldBootstrapReplica.Compatible(local, changed), "mod content mismatch accepted");
        Throws<ArgumentException>(() =>
            WorldBootstrapValidator.ValidateManifest(local with { RimWorldVersion = "1.7.0" }));
        Throws<ArgumentException>(() =>
            WorldBootstrapValidator.ValidateManifest(local with
            {
                Mods = new[] { local.Mods[0], local.Mods[0] with { PackageId = "LUDEON.RIMWORLD" } }
            }));
        Throws<ArgumentException>(() =>
            WorldBootstrapValidator.ValidateManifest(local with { DefCatalogSha256 = "not-sha256" }));
    }

    public static void AtomicAdmission()
    {
        var sample = Create();
        var client = new ClientWorldBootstrapReplica(sample.Manifest, sample.WorldId);
        Assert(client.TryInstall(sample) == BootstrapAdmissionResult.Accepted, "initial install");
        Assert(client.TryInstall(sample with { WorldId = Guid.NewGuid() }) ==
            BootstrapAdmissionResult.WrongWorld, "wrong-world accepted");
        Assert(client.TryInstall(sample with { Manifest = sample.Manifest with { DefCatalogSha256 = HashB } }) ==
            BootstrapAdmissionResult.IncompatibleContent, "incompatible client accepted");
        Assert(client.TryInstall(sample with { NetworkVersion = 9 }) ==
            BootstrapAdmissionResult.StaleRevision, "older network state accepted");
        Assert(client.TryInstall(sample with
        {
            NetworkVersion = 11,
            Maps = new[] { sample.Maps[0] with { Terrain = new byte[2] } }
        }) == BootstrapAdmissionResult.InvalidState, "invalid map accepted");
        Assert(client.NetworkVersion == 10 &&
            client.GetSnapshotCopy().Maps.Count == 2, "invalid admission mutated client");
        Assert(client.TryInstall(sample with
        {
            NetworkVersion = 11,
            GameStateVersion = 101
        }) == BootstrapAdmissionResult.Accepted, "newer valid revision rejected");
    }

    public static void DefensiveCopies()
    {
        var sample = Create();
        var client = new ClientWorldBootstrapReplica(sample.Manifest);
        Assert(client.TryInstall(sample) == BootstrapAdmissionResult.Accepted, "initial install");
        var baselineDigest = WorldBootstrapCodec.ComputeSha256(client.GetSnapshotCopy());
        sample.Maps[0].Terrain[0] = 1;
        var copy = client.GetSnapshotCopy();
        copy.Maps[0].Terrain[0] = 1;
        Assert(WorldBootstrapCodec.ComputeSha256(client.GetSnapshotCopy()) == baselineDigest,
            "caller altered installed terrain");
        var ordered = client.GetSnapshotCopy();
        var reverse = ordered with
        {
            Maps = ordered.Maps.Reverse().ToArray(),
            Entities = ordered.Entities.Reverse().ToArray()
        };
        Assert(WorldBootstrapCodec.ComputeSha256(reverse) == baselineDigest,
            "unordered input changed canonical digest");
        var changed = ordered with
        {
            Maps = ordered.Maps.Select(m => m.DomainId == 1
                ? m with { Terrain = new byte[] { 1, 0, 1, 0 } } : m).ToArray()
        };
        Assert(WorldBootstrapCodec.ComputeSha256(changed) != baselineDigest,
            "data corruption not reflected in digest");
    }

    public static void MalformedPayload()
    {
        var valid = WorldBootstrapCodec.Encode(Create());
        Throws<ArgumentException>(() => WorldBootstrapCodec.Decode(new byte[WorldBootstrapFormat.MaxBytes + 1]));
        Throws<System.Text.Json.JsonException>(() => WorldBootstrapCodec.Decode("{malformed"u8.ToArray()));
        Throws<System.Text.Json.JsonException>(() => WorldBootstrapCodec.Decode(
            valid.Take(valid.Length - 1).ToArray()));
        Bad(Create() with { FormatVersion = 1 });
        Bad(Create() with { WorldId = Guid.Empty });
        var modified = System.Text.Encoding.UTF8.GetString(valid);
        // Unknown JSON members are not tolerated.
        var unknown = modified.Insert(1, "\"Unknown\":0,");
        Throws<System.Text.Json.JsonException>(() =>
            WorldBootstrapCodec.Decode(System.Text.Encoding.UTF8.GetBytes(unknown)));
    }

    private static WorldBootstrapV2 Create()
    {
        var manifest = new ContentManifest("1.6.4566",
            new[]
            {
                new ModFingerprint("ludeon.rimworld", "1.6", HashA),
                new ModFingerprint("example.colony", "2.3", HashB)
            }, HashC);

        // Intentionally out-of-order for deterministic normalization tests.
        var map2 = new MapLayerSnapshot(2, 301, 2, 2,
            new[] { "Soil", "Sand" }, new byte[] { 0, 1, 1, 0 },
            new[] { "RoofRockThin" }, new byte[] { 0, 1, 0, 0 },
            new byte[] { 0b0000_0101 });
        var map1 = new MapLayerSnapshot(1, 300, 2, 2,
            new[] { "Soil", "Sand" }, new byte[] { 0, 1, 1, 0 },
            Array.Empty<string>(), new byte[4], new byte[] { 0b0000_0001 });
        var building = new EntityPresentation(200, 1, 0, "WoodLog", 0, 0, 0, 10,
            PresentationKind.Thing, null);
        var pawn = new EntityPresentation(100, 2, 9, "Human", 1, 1, 2, 1,
            PresentationKind.Pawn, new PawnPresentation(false, false, false, "Goto"));
        return new WorldBootstrapV2(WorldBootstrapFormat.Version, Guid.NewGuid(),
            10, 100, manifest, new[] { map2, map1 }, new[] { building, pawn });
    }

    private static void Bad(WorldBootstrapV2 candidate) =>
        Throws<ArgumentException>(() => WorldBootstrapValidator.ValidateAndCopy(candidate));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
