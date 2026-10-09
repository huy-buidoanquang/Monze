using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Monze.Application;
using Monze.Infrastructure.Caching;
using Monze.Testing.Harness;
using StackExchange.Redis;

namespace Monze.Campaign.Component;

/// <summary>
/// C-REDIS: the read-model cache on the campaign Redis. Measures L2 write
/// and read latency (reads from a cache whose L1 is cold) and how long an
/// invalidation by one instance takes to evict the key from a second
/// instance's L1 over pub/sub.
/// </summary>
public static class RedisCacheScenario
{
    private static readonly TimeSpan PropagationTimeout = TimeSpan.FromSeconds(2);

    public static async Task<IReadOnlyList<CampaignArtifact>> RunAsync(ComponentContext context)
    {
        var keys = context.Full ? 5_000 : 1_000;
        var rounds = context.Full ? 500 : 100;
        var artifact = new CampaignArtifact("component", "C-REDIS", $"Redis L2: {keys:N0} key, {rounds} lần lan truyền invalidation giữa 2 instance");
        if (context.Redis is null)
        {
            artifact.Block("Campaign không chạy Redis.");
            return [artifact];
        }

        var environment = "component-" + Guid.NewGuid().ToString("N")[..12];
        using var first = await ConnectAsync(context.Redis);
        using var second = await ConnectAsync(context.Redis);
        using var writer = new MonzeReadModelCache(first, environment, 1, NullLogger<MonzeReadModelCache>.Instance);
        using var reader = new MonzeReadModelCache(second, environment, 1, NullLogger<MonzeReadModelCache>.Instance);
        await Task.Delay(TimeSpan.FromMilliseconds(500)); // Both instances subscribe to invalidations in the background.

        var writes = new LatencyHistogram();
        var reads = new LatencyHistogram();
        long writeFailures = 0;
        long readMisses = 0;
        for (var i = 0; i < keys; i++)
        {
            var started = Stopwatch.GetTimestamp();
            if (!await writer.SetAsync(1, "settings", "k" + i, new ReadModelCacheEntry(1, "{\"v\":1}"), TimeSpan.FromMinutes(5), CancellationToken.None))
            {
                writeFailures++;
            }

            writes.Record(Stopwatch.GetElapsedTime(started));
        }

        for (var i = 0; i < keys; i++)
        {
            var started = Stopwatch.GetTimestamp();
            if (await reader.GetAsync(1, "settings", "k" + i, CancellationToken.None) is null)
            {
                readMisses++;
            }

            reads.Record(Stopwatch.GetElapsedTime(started));
        }

        var propagation = new LatencyHistogram();
        long lost = 0;
        for (var round = 0; round < rounds; round++)
        {
            var id = "p" + round;
            await writer.SetAsync(1, "settings", id, new ReadModelCacheEntry(1, "{\"v\":1}"), TimeSpan.FromMinutes(5), CancellationToken.None);
            await reader.GetAsync(1, "settings", id, CancellationToken.None);
            var started = Stopwatch.GetTimestamp();
            await writer.InvalidateAsync(1, "settings", id, 2, CancellationToken.None);
            while (await reader.GetAsync(1, "settings", id, CancellationToken.None) is { Version: < 2 })
            {
                if (Stopwatch.GetElapsedTime(started) > PropagationTimeout)
                {
                    lost++;
                    break;
                }

                await Task.Yield();
            }

            propagation.Record(Stopwatch.GetElapsedTime(started));
        }

        artifact.Metric("keys", keys)
            .Latency("write", writes)
            .Latency("read", reads)
            .Latency("invalidation", propagation)
            .Invariant("round-trip", $"{keys:N0} key ghi và đọc lại được từ instance thứ hai", $"{writeFailures:N0} ghi lỗi, {readMisses:N0} miss", writeFailures == 0 && readMisses == 0)
            .Invariant("invalidation-delivered", $"instance thứ hai bỏ bản cũ trong {PropagationTimeout.TotalSeconds:0} s", $"{lost:N0}/{rounds} không tới", lost == 0)
            .P99AtMost("read-p99", reads, 10, "kỳ vọng campaign cho Redis loopback")
            .P99AtMost("invalidation-p99", propagation, 100, "kỳ vọng campaign cho pub/sub loopback");
        return [artifact];
    }

    private static async Task<ConnectionMultiplexer> ConnectAsync(string connection)
    {
        // The same timeouts as MonzeRedisRegistration.
        var options = ConfigurationOptions.Parse(connection);
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 3;
        options.ConnectTimeout = 1_000;
        options.AsyncTimeout = 1_000;
        options.SyncTimeout = 1_000;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }
}
