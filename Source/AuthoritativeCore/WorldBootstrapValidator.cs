namespace Multiplayer.Authoritative;

// Validation is applied to BOTH server-produced and client-received DTOs.
// A successful return is a deep, canonical copy: callers cannot mutate the
// accepted map byte arrays or entity/manifest collections afterwards.
public static class WorldBootstrapValidator
{
    public static WorldBootstrapV2 ValidateAndCopy(WorldBootstrapV2 snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.FormatVersion != WorldBootstrapFormat.Version ||
            snapshot.WorldId == Guid.Empty || snapshot.NetworkVersion < 0 ||
            snapshot.GameStateVersion < 0 || snapshot.Maps is null ||
            snapshot.Maps.Count == 0 || snapshot.Maps.Count > WorldBootstrapFormat.MaxMaps ||
            snapshot.Entities is null || snapshot.Entities.Count > WorldBootstrapFormat.MaxEntities)
            throw new ArgumentException("Invalid bootstrap header or collection size", nameof(snapshot));

        var manifest = ValidateManifest(snapshot.Manifest);
        var maps = new Dictionary<int, MapLayerSnapshot>();
        foreach (var map in snapshot.Maps)
        {
            if (map is null || map.DomainId <= 0 || maps.ContainsKey(map.DomainId) ||
                map.WorldTile < 0 || map.Width <= 0 || map.Height <= 0 ||
                map.Width > WorldBootstrapFormat.MaxMapSide ||
                map.Height > WorldBootstrapFormat.MaxMapSide)
                throw new ArgumentException("Invalid or duplicate map domain", nameof(snapshot));

            var cells = checked(map.Width * map.Height);
            var terrainDefs = ValidatePalette(map.TerrainDefs, 256, allowEmpty: false);
            var roofDefs = ValidatePalette(map.RoofDefs, 255, allowEmpty: true);
            if (map.Terrain is null || map.Terrain.Length != cells ||
                map.Roof is null || map.Roof.Length != cells ||
                map.FogBits is null || map.FogBits.Length != (cells + 7) / 8)
                throw new ArgumentException("Map layer length differs from map dimensions", nameof(snapshot));
            for (var i = 0; i < cells; i++)
                if (map.Terrain[i] >= terrainDefs.Length || map.Roof[i] > roofDefs.Length)
                    throw new ArgumentException("Map layer references an unknown palette entry", nameof(snapshot));

            // Reject high bits in the final fog byte for deterministic encoding.
            var spareBits = map.FogBits.Length * 8 - cells;
            if (spareBits > 0 && (map.FogBits[map.FogBits.Length - 1] >> (8 - spareBits)) != 0)
                throw new ArgumentException("Unused fog bits must be zero", nameof(snapshot));

            maps.Add(map.DomainId, new MapLayerSnapshot(map.DomainId, map.WorldTile, map.Width,
                map.Height, terrainDefs, (byte[])map.Terrain.Clone(), roofDefs,
                (byte[])map.Roof.Clone(), (byte[])map.FogBits.Clone()));
        }

        var seen = new HashSet<long>();
        var entities = new List<EntityPresentation>(snapshot.Entities.Count);
        foreach (var entity in snapshot.Entities)
        {
            if (entity is null || entity.Id <= 0 || !seen.Add(entity.Id) ||
                !maps.TryGetValue(entity.DomainId, out var map) ||
                entity.FactionId < 0 || !SafeName(entity.DefName) ||
                entity.X < 0 || entity.X >= map.Width ||
                entity.Z < 0 || entity.Z >= map.Height ||
                entity.Rotation < 0 || entity.Rotation > 3 ||
                entity.StackCount < 1 || entity.StackCount > 1_000_000 ||
                entity.Kind is not (PresentationKind.Thing or PresentationKind.Pawn) ||
                (entity.Kind == PresentationKind.Pawn) != (entity.Pawn is not null) ||
                (entity.Pawn is not null && entity.StackCount != 1) ||
                (entity.Pawn?.CurrentJobDef is { } job && !SafeName(job)))
                throw new ArgumentException("Invalid entity or duplicate stable network ID", nameof(snapshot));
            entities.Add(entity with { });
        }

        return snapshot with
        {
            Manifest = manifest,
            Maps = maps.Values.OrderBy(m => m.DomainId).ToArray(),
            Entities = entities.OrderBy(e => e.Id).ToArray()
        };
    }

    public static ContentManifest ValidateManifest(ContentManifest manifest)
    {
        if (manifest is null || manifest.Mods is null ||
            manifest.Mods.Count == 0 || manifest.Mods.Count > 1024 ||
            !SupportedGameVersion(manifest.RimWorldVersion) ||
            !IsSha256(manifest.DefCatalogSha256))
            throw new ArgumentException("Missing or unsupported RimWorld content manifest", nameof(manifest));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mods = new List<ModFingerprint>(manifest.Mods.Count);
        foreach (var mod in manifest.Mods)
        {
            if (mod is null || !SafeName(mod.PackageId) ||
                !SafeName(mod.Version) || !IsSha256(mod.ContentSha256) ||
                !seen.Add(mod.PackageId))
                throw new ArgumentException("Invalid or duplicate mod fingerprint", nameof(manifest));
            mods.Add(mod with
            {
                PackageId = mod.PackageId.ToLowerInvariant(),
                ContentSha256 = mod.ContentSha256.ToLowerInvariant()
            });
        }
        return new ContentManifest(manifest.RimWorldVersion,
            mods.ToArray(), manifest.DefCatalogSha256.ToLowerInvariant());
    }

    private static string[] ValidatePalette(IReadOnlyList<string>? items, int max,
        bool allowEmpty)
    {
        if (items is null || items.Count > max || (!allowEmpty && items.Count == 0))
            throw new ArgumentException("Invalid map palette length", nameof(items));
        var values = new HashSet<string>(StringComparer.Ordinal);
        var result = new string[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var name = items[i];
            if (!SafeName(name) || !values.Add(name))
                throw new ArgumentException("Invalid or duplicate Def name", nameof(items));
            result[i] = name;
        }
        return result;
    }

    private static bool SupportedGameVersion(string? value) =>
        value is not null && (value == "1.6" || value.StartsWith("1.6.", StringComparison.Ordinal))
            && SafeName(value);

    private static bool SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            return false;
        foreach (var c in name)
            if (char.IsControl(c) || char.IsWhiteSpace(c))
                return false;
        return true;
    }

    private static bool IsSha256(string? hash)
    {
        if (hash is null || hash.Length != 64)
            return false;
        foreach (var c in hash)
            if (!((c >= '0' && c <= '9') ||
                  (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }
}
