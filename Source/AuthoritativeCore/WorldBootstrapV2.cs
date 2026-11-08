namespace Multiplayer.Authoritative;

// An independently versioned, server-produced initial VIEW of a RimWorld map.
// This does not replace a Verse save, execute game rules, or create client Things.
// It deliberately does not replace WorldSnapshot v1 used by the local TCP probe.
public static class WorldBootstrapFormat
{
    public const int Version = 2;
    public const int MaxBytes = 8 * 1024 * 1024;
    public const int MaxMaps = 16;
    public const int MaxMapSide = 512;
    public const int MaxEntities = 100_000;
}

public sealed record ModFingerprint(string PackageId, string Version, string ContentSha256);

// Mod order matters; DefCatalogSha256 must be calculated by an eventual Verse
// adapter from the actual resolved Def catalog, not a user-provided declaration.
public sealed record ContentManifest(
    string RimWorldVersion,
    IReadOnlyList<ModFingerprint> Mods,
    string DefCatalogSha256);

// Packed map layers: terrain/roof are 8-bit indexes into per-map palettes.
// Roof index 0 = no roof; indexes 1..RoofDefs.Count map to RoofDefs[index-1].
// FogBits is a packed bitset, one bit per cell (row-major X+Z*Width).
public sealed record MapLayerSnapshot(
    int DomainId,
    int WorldTile,
    int Width,
    int Height,
    IReadOnlyList<string> TerrainDefs,
    byte[] Terrain,
    IReadOnlyList<string> RoofDefs,
    byte[] Roof,
    byte[] FogBits);

public enum PresentationKind
{
    Thing = 1,
    Pawn = 2
}

// Presentation only; a full pawn Scribe graph, health Hediffs, trackers,
// equipment and inventory are intentionally NOT represented.
public sealed record PawnPresentation(
    bool Drafted,
    bool Downed,
    bool Dead,
    string? CurrentJobDef);

public sealed record EntityPresentation(
    long Id,
    int DomainId,
    int FactionId,
    string DefName,
    int X,
    int Z,
    int Rotation,
    int StackCount,
    PresentationKind Kind,
    PawnPresentation? Pawn);

public sealed record WorldBootstrapV2(
    int FormatVersion,
    Guid WorldId,
    long NetworkVersion,
    long GameStateVersion,
    ContentManifest Manifest,
    IReadOnlyList<MapLayerSnapshot> Maps,
    IReadOnlyList<EntityPresentation> Entities);
