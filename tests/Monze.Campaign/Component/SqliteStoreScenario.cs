using System.Collections.Concurrent;
using System.Diagnostics;
using Mezon.Net.Sdk.Caching.Sqlite;
using Microsoft.Data.Sqlite;
using Monze.Testing.Harness;

namespace Monze.Campaign.Component;

/// <summary>
/// C-SQLITE: the SDK message-history store Monze writes for persisted
/// channels, driven open-loop at 200, 1,000 and 2,000 messages/s from four
/// producers. Enqueueing is synchronous; the SDK writes on a background
/// pump, so the measure is how long a sampled message takes to become
/// readable and how long the final flush takes.
/// </summary>
public static class SqliteStoreScenario
{
    private const int Producers = 4;
    private const int SampleEvery = 100;
    private static readonly TimeSpan VisibleWithin = TimeSpan.FromSeconds(10);

    public static async Task<IReadOnlyList<CampaignArtifact>> RunAsync(ComponentContext context)
    {
        var duration = TimeSpan.FromSeconds(context.Full ? 30 : 8);
        var artifact = new CampaignArtifact("component", "C-SQLITE", $"SqliteMessageStore 200/1.000/2.000 msg/s trong {duration.TotalSeconds:0} s mỗi mức");
        foreach (var rate in new[] { 200, 1_000, 2_000 })
        {
            var run = await RunRateAsync(rate, duration);
            var prefix = $"r{rate}.";
            artifact.Metric(prefix + "achievedPerSecond", run.Sent / duration.TotalSeconds)
                .Latency(prefix + "visible", run.Visibility)
                .Metric(prefix + "flushMs", run.Flush.TotalMilliseconds)
                .Latency(prefix + "generatorLag", run.Lag)
                .Invariant($"{prefix}rate", $"đạt ≥ 95 % của {rate:N0} msg/s", $"{run.Sent / duration.TotalSeconds:0} msg/s", run.Sent >= rate * duration.TotalSeconds * 0.95)
                .Invariant($"{prefix}durable", $"mọi mẫu (1/{SampleEvery}) đọc lại được trong {VisibleWithin.TotalSeconds:0} s", $"{run.Lost:N0} mất / {run.Visibility.Count + run.Lost:N0}", run.Lost == 0)
                .Invariant($"{prefix}flush", "flush cuối ≤ 5 s", $"{run.Flush.TotalMilliseconds:0} ms", run.Flush <= TimeSpan.FromSeconds(5))
                .P99AtMost($"{prefix}visible-p99", run.Visibility, 1_000, "kỳ vọng campaign: lịch sử đọc lại trong 1 s");
        }

        return [artifact];
    }

    private static async Task<RateResult> RunRateAsync(int rate, TimeSpan duration)
    {
        var directory = Directory.CreateTempSubdirectory("monze-c-sqlite-").FullName;
        try
        {
            var store = await SqliteMessageStore.OpenAsync(Path.Combine(directory, "messages.db"));
            var samples = new ConcurrentQueue<(long Channel, long Message, long Enqueued)>();
            var visibility = new LatencyHistogram();
            var lag = new LatencyHistogram();
            long sent = 0;
            long lost = 0;
            using var producing = new CancellationTokenSource();
            var checker = Task.Run(async () =>
            {
                while (!producing.IsCancellationRequested || !samples.IsEmpty)
                {
                    if (!samples.TryDequeue(out var sample))
                    {
                        await Task.Delay(1);
                        continue;
                    }

                    var visible = true;
                    while (await store.TryGetMessageAsync(sample.Channel, sample.Message) is null)
                    {
                        if (Stopwatch.GetElapsedTime(sample.Enqueued) > VisibleWithin)
                        {
                            visible = false;
                            Interlocked.Increment(ref lost);
                            break;
                        }

                        await Task.Yield();
                    }

                    if (visible)
                    {
                        visibility.Record(Stopwatch.GetElapsedTime(sample.Enqueued));
                    }
                }
            });

            var start = Stopwatch.GetTimestamp();
            var interval = (double)Stopwatch.Frequency * Producers / rate;
            var end = start + (long)(duration.TotalSeconds * Stopwatch.Frequency);
            var threads = Enumerable.Range(0, Producers).Select(producer => new Thread(() =>
            {
                for (long i = 0; ; i++)
                {
                    var due = start + (long)((i + producer / (double)Producers) * interval);
                    if (due >= end)
                    {
                        return;
                    }

                    while (Stopwatch.GetTimestamp() < due)
                    {
                        Thread.Yield();
                    }

                    lag.Record(Stopwatch.GetElapsedTime(due));
                    var messageId = 1_000_000_000L + i * Producers + producer;
                    var channelId = 10 + messageId % 20;
                    var write = store.UpsertMessageAsync(
                        new MessageSnapshot
                        {
                            MessageId = messageId,
                            ChannelId = channelId,
                            ClanId = 1,
                            SenderId = 5,
                            Content = "tin nhắn tải " + messageId,
                            CreateTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        },
                        messageId);
                    if (!write.IsCompletedSuccessfully)
                    {
                        write.AsTask().GetAwaiter().GetResult();
                    }

                    Interlocked.Increment(ref sent);
                    if (messageId % SampleEvery == 0)
                    {
                        samples.Enqueue((channelId, messageId, Stopwatch.GetTimestamp()));
                    }
                }
            }) { IsBackground = true }).ToList();
            threads.ForEach(static thread => thread.Start());
            threads.ForEach(static thread => thread.Join());

            var flushStarted = Stopwatch.GetTimestamp();
            await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var flush = Stopwatch.GetElapsedTime(flushStarted);
            await producing.CancelAsync();
            await checker;
            await store.DisposeAsync();
            return new RateResult(Interlocked.Read(ref sent), visibility, lag, flush, Interlocked.Read(ref lost));
        }
        finally
        {
            // Microsoft.Data.Sqlite pools connections, which keep the file open after the store is disposed.
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record RateResult(long Sent, LatencyHistogram Visibility, LatencyHistogram Lag, TimeSpan Flush, long Lost);
}
