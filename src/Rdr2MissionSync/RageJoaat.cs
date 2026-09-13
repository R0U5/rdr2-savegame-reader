namespace Rdr2MissionSync;

/// <summary>
/// RAGE engine joaat hash used by the PSO schema analyzer. The reference
/// implementation lives in <c>Rdr2PsoSchemaAnalyzer</c> and is private there,
/// so this library keeps its own copy to avoid an InternalsVisibleTo bridge.
/// </summary>
internal static class RageJoaat
{
    public static uint Compute(string value)
    {
        uint hash = 0;
        foreach (var character in value)
        {
            var normalized = char.ToLowerInvariant(character);
            hash += normalized;
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }
}