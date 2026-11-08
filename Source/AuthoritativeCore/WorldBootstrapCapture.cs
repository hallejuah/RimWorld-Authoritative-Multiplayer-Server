namespace Multiplayer.Authoritative;

// Runtime map extraction is performed by the SERVER's game-thread adapter.
// These callbacks are read-only; implementations must not run game logic or
// disclose untrusted client-provided map data.
public interface IWorldBootstrapMapSource
{
    int DomainId { get; }
    int WorldTile { get; }
    int Width { get; }
    int Height { get; }
    string TerrainDefAt(int x, int z);
    string? RoofDefAt(int x, int z);
    bool FoggedAt(int x, int z);
    IReadOnlyList<EntityPresentation> ReadSpawnedEntities();
}

// Presentation-only capture from one or more real game-state readers. This
// class does not emulate RimWorld, load a save or mint content fingerprints.
// A caller MUST use the server's authenticated world ID and a manifest built
// from the actual active game installation. Do not fill with placeholder hashes.
public static class WorldBootstrapCapture
{
    public static WorldBootstrapV2 Capture(
        Guid worldId, long networkVersion, long gameStateVersion,
        ContentManifest trustedManifest, IReadOnlyList<IWorldBootstrapMapSource> sources)
    {
        if (sources is null || sources.Count == 0 ||
            sources.Count > WorldBootstrapFormat.MaxMaps)
            throw new ArgumentException("Invalid number of source maps", nameof(sources));
        // Fail before reading a potentially large map if the manifest is bad.
        var manifest = WorldBootstrapValidator.ValidateManifest(trustedManifest);
        if (worldId == Guid.Empty || networkVersion < 0 || gameStateVersion < 0)
            throw new ArgumentException("Invalid server world or revision metadata");

        var maps = new List<MapLayerSnapshot>(sources.Count);
        var entities = new List<EntityPresentation>();
        var domains = new HashSet<int>();

        foreach (var source in sources)
        {
            if (source is null || source.DomainId <= 0 ||
                !domains.Add(source.DomainId) || source.WorldTile < 0 ||
                source.Width <= 0 || source.Width > WorldBootstrapFormat.MaxMapSide ||
                source.Height <= 0 || source.Height > WorldBootstrapFormat.MaxMapSide)
                throw new ArgumentException("Invalid source map dimensions or domain", nameof(sources));

            var cells = checked(source.Width * source.Height);
            var terrain = new byte[cells];
            var roofs = new byte[cells];
            var fog = new byte[(cells + 7) / 8];
            var terrainDefs = new List<string>();
            var roofDefs = new List<string>();
            var terrainPalette = new Dictionary<string, byte>(StringComparer.Ordinal);
            var roofPalette = new Dictionary<string, byte>(StringComparer.Ordinal);

            // Stable encoding order is X increasing, then Z increasing.
            for (var z = 0; z < source.Height; z++)
            for (var x = 0; x < source.Width; x++)
            {
                var offset = z * source.Width + x;
                var terrainDef = source.TerrainDefAt(x, z);
                if (string.IsNullOrWhiteSpace(terrainDef))
                    throw new InvalidOperationException("A real map cell has no terrain definition");
                if (!terrainPalette.TryGetValue(terrainDef, out var terrainIndex))
                {
                    if (terrainDefs.Count >= 256)
                        throw new InvalidOperationException("Terrain palette exceeds the v2 format");
                    terrainIndex = checked((byte)terrainDefs.Count);
                    terrainDefs.Add(terrainDef);
                    terrainPalette.Add(terrainDef, terrainIndex);
                }
                terrain[offset] = terrainIndex;

                var roofDef = source.RoofDefAt(x, z);
                if (roofDef is not null)
                {
                    if (string.IsNullOrWhiteSpace(roofDef))
                        throw new InvalidOperationException("Map roof has an invalid definition");
                    if (!roofPalette.TryGetValue(roofDef, out var roofIndex))
                    {
                        if (roofDefs.Count >= 255)
                            throw new InvalidOperationException("Roof palette exceeds the v2 format");
                        roofIndex = checked((byte)(roofDefs.Count + 1));
                        roofDefs.Add(roofDef);
                        roofPalette.Add(roofDef, roofIndex);
                    }
                    roofs[offset] = roofIndex;
                }
                if (source.FoggedAt(x, z))
                    fog[offset / 8] |= (byte)(1 << (offset % 8));
            }

            maps.Add(new MapLayerSnapshot(source.DomainId, source.WorldTile,
                source.Width, source.Height, terrainDefs.ToArray(), terrain,
                roofDefs.ToArray(), roofs, fog));

            var spawned = source.ReadSpawnedEntities()
                ?? throw new InvalidOperationException("Map returned no entity collection");
            if (spawned.Count > WorldBootstrapFormat.MaxEntities - entities.Count)
                throw new InvalidOperationException("Too many spawned entities for one bootstrap");
            entities.AddRange(spawned);
        }

        var candidate = new WorldBootstrapV2(WorldBootstrapFormat.Version,
            worldId, networkVersion, gameStateVersion, manifest, maps.ToArray(),
            entities.ToArray());
        // Full referential validation and defensive copy must happen before
        // publishing. Also fail closed if encoded JSON exceeds the wire budget.
        var result = WorldBootstrapValidator.ValidateAndCopy(candidate);
        WorldBootstrapCodec.Encode(result);
        return result;
    }
}
