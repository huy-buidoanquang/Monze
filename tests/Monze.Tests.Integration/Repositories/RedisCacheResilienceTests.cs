using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Monze.Infrastructure.Caching;
using Monze.Testing;
using StackExchange.Redis;
using Xunit;

namespace Monze.Tests.Repositories;

/// <summary>
/// Redis is only a cache: when it fails, reads return "miss" so callers fall
/// back to PostgreSQL, and the cache stops calling Redis for the 5 s cooldown.
/// Known gap DEF-01: a RedisTimeoutException is a TimeoutException, not a
/// RedisException, so a slow Redis escapes the fallback and never starts the
/// cooldown.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisCacheResilienceTests
{
    [RedisFact]
    [Req("REQ-CACHE-001")]
    [Covers("metric:monze.cache.redis.error")]
    public async Task A_failed_redis_is_a_miss_and_starts_the_cooldown()
    {
        // A loopback port nothing listens on: every command fails with a RedisConnectionException.
        var options = Options("127.0.0.1:56398");
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(options);
        using var errors = new CounterCapture("monze.cache.redis.error");
        using var cache = new MonzeReadModelCache(multiplexer, "resilience-" + Guid.NewGuid().ToString("N"), 1, NullLogger<MonzeReadModelCache>.Instance);

        Assert.Null(await cache.GetAsync(1, "settings", "a", CancellationToken.None));
        var watch = Stopwatch.StartNew();
        Assert.Null(await cache.GetAsync(1, "settings", "b", CancellationToken.None));
        Assert.False(await cache.SetAsync(1, "settings", "b", new Monze.Application.ReadModelCacheEntry(1, "x"), TimeSpan.FromMinutes(1), CancellationToken.None));
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 500, $"cooldown calls took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(1, errors.Total);
    }

    [RedisFact]
    [Req("REQ-CACHE-001")]
    public async Task A_slow_redis_is_a_miss_and_starts_the_cooldown()
    {
        var connection = TestRedis.ConnectionString;
        var adminOptions = ConfigurationOptions.Parse(connection);
        adminOptions.AllowAdmin = true;
        using var admin = await ConnectionMultiplexer.ConnectAsync(adminOptions);
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(Options(connection));
        using var cache = new MonzeReadModelCache(multiplexer, "timeout-" + Guid.NewGuid().ToString("N"), 1, NullLogger<MonzeReadModelCache>.Instance);
        await cache.GetAsync(1, "settings", "warm", CancellationToken.None);
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        await server.ExecuteAsync("CLIENT", "PAUSE", "2500", "ALL");
        try
        {
            await KnownDefect.ExpectFailureAsync("DEF-01", async () =>
            {
                Assert.Null(await cache.GetAsync(1, "settings", "a", CancellationToken.None));
                var watch = Stopwatch.StartNew();
                Assert.Null(await cache.GetAsync(1, "settings", "b", CancellationToken.None));
                Assert.True(watch.ElapsedMilliseconds < 500, $"second read took {watch.ElapsedMilliseconds} ms");
            });
        }
        finally
        {
            await Task.Delay(2_600);
            await server.ExecuteAsync("CLIENT", "UNPAUSE");
        }
    }

    private static ConfigurationOptions Options(string connection)
    {
        // The same timeouts as MonzeRedisRegistration.
        var options = ConfigurationOptions.Parse(connection);
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 1;
        options.ConnectTimeout = 1_000;
        options.AsyncTimeout = 1_000;
        options.SyncTimeout = 1_000;
        return options;
    }

    private sealed class CounterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public CounterCapture(string name)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Monze.Cache" && instrument.Name == name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _total, value));
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }
}

/// <summary>Redis tests run one at a time: CLIENT PAUSE affects every client of the campaign Redis.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RedisCollection
{
    public const string Name = "campaign-redis";
}
