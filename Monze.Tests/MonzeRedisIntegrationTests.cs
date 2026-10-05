using Microsoft.Extensions.Logging.Abstractions;
using Monze.Application;
using Monze.Infrastructure.Caching;
using StackExchange.Redis;
using Xunit;

namespace Monze.Tests;

public sealed class MonzeRedisIntegrationTests
{
    [Fact]
    public async Task Two_instances_preserve_invalidation_tombstone_and_stale_writer_safety()
    {
        var connectionString = Environment.GetEnvironmentVariable("MONZE_REDIS_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 2;
        options.ConnectTimeout = 1_000;
        options.AsyncTimeout = 1_000;
        options.SyncTimeout = 1_000;

        using var multiplexerA = await ConnectionMultiplexer.ConnectAsync(options);
        using var multiplexerB = await ConnectionMultiplexer.ConnectAsync(options);
        var environment = "correctness-" + Guid.NewGuid().ToString("N");
        const long botId = 2061341035941859328;

        using var cacheA = new MonzeReadModelCache(
            multiplexerA,
            environment,
            botId,
            NullLogger<MonzeReadModelCache>.Instance);
        using var cacheB = new MonzeReadModelCache(
            multiplexerB,
            environment,
            botId,
            NullLogger<MonzeReadModelCache>.Instance);

        await WaitUntilConnectedAsync(cacheA, cacheB);

        const long clanId = 2104288434238525440;
        const string kind = "correctness";
        const string id = "redis-race";

        for (var index = 0; index < 16; index++)
        {
            var raceId = "cross-instance-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(await cacheA.SetAsync(
                clanId,
                kind,
                raceId,
                new ReadModelCacheEntry(1, "v1"),
                TimeSpan.FromMinutes(1),
                CancellationToken.None));

            var crossInstanceRead = await cacheB.GetAsync(
                clanId,
                kind,
                raceId,
                CancellationToken.None);
            Assert.Equal(1, crossInstanceRead?.Version);
            Assert.Equal("v1", crossInstanceRead?.Payload);
        }

        Assert.True(await cacheA.SetAsync(
            clanId,
            kind,
            id,
            new ReadModelCacheEntry(1, "v1"),
            TimeSpan.FromMinutes(1),
            CancellationToken.None));

        var initial = await cacheB.GetAsync(clanId, kind, id, CancellationToken.None);
        Assert.Equal(1, initial?.Version);
        Assert.Equal("v1", initial?.Payload);

        Assert.True(await cacheA.SetAsync(
            clanId,
            kind,
            id,
            new ReadModelCacheEntry(2, "v2"),
            TimeSpan.FromMinutes(1),
            CancellationToken.None));

        await WaitForVersionAsync(cacheB, clanId, kind, id, 2);

        await cacheA.InvalidateAsync(clanId, kind, id, 3, CancellationToken.None);
        var tombstone = await WaitForVersionAsync(cacheB, clanId, kind, id, 3);
        Assert.Equal(string.Empty, tombstone.Payload);

        Assert.False(await cacheB.SetAsync(
            clanId,
            kind,
            id,
            new ReadModelCacheEntry(2, "stale"),
            TimeSpan.FromMinutes(1),
            CancellationToken.None));

        var final = await cacheA.GetAsync(clanId, kind, id, CancellationToken.None);
        Assert.Equal(3, final?.Version);
        Assert.Equal(string.Empty, final?.Payload);
    }

    private static async Task WaitUntilConnectedAsync(
        MonzeReadModelCache cacheA,
        MonzeReadModelCache cacheB)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while ((!cacheA.IsConnected || !cacheB.IsConnected)
            && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(cacheA.IsConnected);
        Assert.True(cacheB.IsConnected);
    }

    private static async Task<ReadModelCacheEntry> WaitForVersionAsync(
        MonzeReadModelCache cache,
        long clanId,
        string kind,
        string id,
        long expectedVersion)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        ReadModelCacheEntry? result = null;
        while (DateTime.UtcNow < deadline)
        {
            result = await cache.GetAsync(clanId, kind, id, CancellationToken.None);
            if (result?.Version == expectedVersion)
            {
                return result.Value;
            }

            await Task.Delay(50);
        }

        Assert.Equal(expectedVersion, result?.Version);
        return result!.Value;
    }
}
