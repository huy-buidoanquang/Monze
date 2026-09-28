namespace Monze.Application;

public sealed class DisabledReadModelCache : IReadModelCache
{
    public ValueTask<ReadModelCacheEntry?> GetAsync(
        long clanId,
        string kind,
        string id,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<ReadModelCacheEntry?>(null);

    public ValueTask<bool> SetAsync(
        long clanId,
        string kind,
        string id,
        ReadModelCacheEntry entry,
        TimeSpan ttl,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(false);

    public ValueTask InvalidateAsync(
        long clanId,
        string kind,
        string id,
        long version,
        CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
