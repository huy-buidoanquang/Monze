using Monze.Application;
using Monze.Infrastructure.Caching;
using Monze.Testing;
using Xunit;

namespace Monze.Tests;

public sealed class MonzeL1CacheTests
{
    [Fact]
    public void Set_and_get_returns_the_current_version()
    {
        using var cache = new MonzeL1Cache();
        var key = new MonzeCacheKey(42, "settings", "current");

        cache.Set(key, new ReadModelCacheEntry(2, "payload"), TimeSpan.FromMinutes(1), 7);

        Assert.True(cache.TryGet(key, out var entry));
        Assert.Equal(2, entry.Version);
        Assert.Equal("payload", entry.Payload);
    }

    [Fact]
    public void Older_version_does_not_replace_a_newer_entry()
    {
        using var cache = new MonzeL1Cache();
        var key = new MonzeCacheKey(42, "settings", "current");

        cache.Set(key, new ReadModelCacheEntry(2, "new"), TimeSpan.FromMinutes(1), 3);
        cache.Set(key, new ReadModelCacheEntry(1, "old"), TimeSpan.FromMinutes(1), 3);

        Assert.True(cache.TryGet(key, out var entry));
        Assert.Equal(2, entry.Version);
        Assert.Equal("new", entry.Payload);
    }

    [Fact]
    public async Task Expired_entry_is_not_returned()
    {
        using var cache = new MonzeL1Cache();
        var key = new MonzeCacheKey(42, "settings", "current");
        cache.Set(key, new ReadModelCacheEntry(1, "payload"), TimeSpan.FromMilliseconds(1), 7);

        await Task.Delay(25);

        Assert.False(cache.TryGet(key, out _));
    }

    [Fact]
    [Req("REQ-TIME-001")]
    public void Expiry_follows_the_injected_clock()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var cache = new MonzeL1Cache(time);
        var key = new MonzeCacheKey(42, "settings", "current");
        cache.Set(key, new ReadModelCacheEntry(1, "payload"), TimeSpan.FromSeconds(10), 7);

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.True(cache.TryGet(key, out _));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(cache.TryGet(key, out _));
    }

    [Fact]
    [Req("REQ-TIME-001")]
    public void Maintenance_timer_runs_on_the_injected_clock()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new MonzeL1Cache(time);
        Assert.Equal(1, time.ActiveTimerCount);

        cache.Dispose();

        Assert.Equal(0, time.ActiveTimerCount);
    }

    /// <summary>
    /// Regression for CAND-30: every write queued an eviction token that was
    /// only dequeued while the cache was over its 64 MiB limit, so a cache
    /// below it kept the token of every replaced, expired or removed entry.
    /// Maintenance now keeps only the tokens of live entries.
    /// </summary>
    [Fact]
    public void Maintenance_drops_eviction_tokens_of_replaced_expired_and_removed_entries()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var cache = new MonzeL1Cache(time);
        var replaced = new MonzeCacheKey(42, "settings", "replaced");
        for (var version = 1; version <= 1_000; version++)
        {
            cache.Set(replaced, new ReadModelCacheEntry(version, "payload"), TimeSpan.FromHours(1), 7);
        }

        for (var i = 0; i < 500; i++)
        {
            cache.Set(new MonzeCacheKey(42, "profile", "expired-" + i), new ReadModelCacheEntry(1, "payload"), TimeSpan.FromSeconds(10), 7);
            var removed = new MonzeCacheKey(42, "profile", "removed-" + i);
            cache.Set(removed, new ReadModelCacheEntry(1, "payload"), TimeSpan.FromHours(1), 7);
            cache.Remove(removed);
        }

        Assert.Equal(2_000, cache.QueuedTokens);

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(1, cache.QueuedTokens);
        Assert.True(cache.TryGet(replaced, out var entry));
        Assert.Equal(1_000, entry.Version);
    }
}
