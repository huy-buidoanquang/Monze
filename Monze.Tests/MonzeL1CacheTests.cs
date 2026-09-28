using Monze.Application;
using Monze.Infrastructure.Caching;
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
}
