namespace Multiplayer.Authoritative;

// RimWorld map.uniqueID can be zero. The standalone scheduler requires
// strictly positive domain IDs, so never use map.uniqueID directly.
public static class MapDomainIds
{
    public static int FromMapUniqueId(int mapUniqueId)
    {
        if (mapUniqueId < 0 || mapUniqueId == int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(mapUniqueId));
        return checked(mapUniqueId + 1);
    }
}
