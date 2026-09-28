namespace Monze.Application;

public interface IReadModelCache
{
    ValueTask<ReadModelCacheEntry?> GetAsync(
        long clanId,
        string kind,
        string id,
        CancellationToken cancellationToken);

    ValueTask<bool> SetAsync(
        long clanId,
        string kind,
        string id,
        ReadModelCacheEntry entry,
        TimeSpan ttl,
        CancellationToken cancellationToken);

    ValueTask InvalidateAsync(
        long clanId,
        string kind,
        string id,
        long version,
        CancellationToken cancellationToken);
}
