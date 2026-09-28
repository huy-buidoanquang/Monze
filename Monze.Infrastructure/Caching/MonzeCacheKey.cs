namespace Monze.Infrastructure.Caching;

internal readonly struct MonzeCacheKey : IEquatable<MonzeCacheKey>
{
    public MonzeCacheKey(long clanId, string kind, string id)
    {
        ClanId = clanId;
        Kind = kind;
        Id = id;
    }

    public long ClanId { get; }

    public string Kind { get; }

    public string Id { get; }

    public bool Equals(MonzeCacheKey other)
        => ClanId == other.ClanId
            && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
            && string.Equals(Id, other.Id, StringComparison.Ordinal);

    public override bool Equals(object? obj)
        => obj is MonzeCacheKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = (hash * 31) + ClanId.GetHashCode();
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(Kind);
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(Id);
            return hash;
        }
    }
}
