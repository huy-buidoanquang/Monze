namespace Monze.Testing;

/// <summary>
/// Deterministic per-generator seeds. The base seed comes from
/// MONZE_CAMPAIGN_SEED so a whole campaign can be replayed; each generator id
/// derives its own stream so adding a generator does not shift the others.
/// </summary>
public static class RunSeed
{
    public const ulong DefaultBase = 0x4D6F6E7A65_0001UL;

    public static ulong Base
        => CampaignEnvironment.Seed is { } text && ulong.TryParse(text, out var parsed)
            ? parsed
            : DefaultBase;

    public static ulong For(string generatorId)
    {
        var hash = Base ^ 14695981039346656037UL;
        foreach (var ch in generatorId)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    public static Random RandomFor(string generatorId)
        => new(unchecked((int)(For(generatorId) ^ (For(generatorId) >> 32))));
}
