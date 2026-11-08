namespace Multiplayer.Authoritative;

// Independent presentation graph, not a Verse save or a gameplay authority.
// WorldBootstrapV2 remains unchanged; WorldViewCheckpoint pairs it with this graph.
public static class EntityGraphFormat
{
    public const int Version = 1;
    public const int MaxBytes = 8 * 1024 * 1024;
    public const int MaxEntities = WorldBootstrapFormat.MaxEntities;
    public const int MaxHolders = 100_000;
    public const int MaxChildrenPerHolder = 4096;
    public const int MaxHolderDepth = 32;
    public const int MaxNameBytes = 256;
}

public enum EntityHolderSlot
{
    None = 0,
    Inventory = 1,
    Equipment = 2,
    Apparel = 3,
    Carry = 4,
    Contents = 5
}

public sealed record EntityGraphMap(int DomainId, int Width, int Height);

// A slot exists even when empty. Holder-only components without a Thing identity
// are deliberately not assigned arbitrary client-minted entity IDs.
public sealed record EntityHolder(long OwnerEntityId, EntityHolderSlot Slot);

// Exactly one location: (0, None) means spawned on the map; otherwise the
// entity is in the named holder slot and its presentation X/Z must both be -1.
public sealed record EntityAttachment(long HolderEntityId, EntityHolderSlot Slot);
public sealed record EntityGraphNode(EntityPresentation Entity, EntityAttachment Attachment);

// Held entities inherit the map DOMAIN, not the faction, of the ultimate root.
// FactionId is presentation data, NOT permission to control or transfer an item.
public sealed record EntityGraphSnapshot(
    int FormatVersion,
    Guid WorldId,
    long NetworkVersion,
    long GameStateVersion,
    IReadOnlyList<EntityGraphMap> Maps,
    IReadOnlyList<EntityGraphNode> Nodes,
    IReadOnlyList<EntityHolder> Holders);
