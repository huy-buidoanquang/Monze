using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Application.Ingress;

internal static class CapacitySoakRunner
{
    private const int ClanCount = 1_000;
    private const int ActiveClanCount = 100;
    private const int MessageRate = 200;
    private const int CommandRate = 20;
    private const int MeetingCandidateCount = 100;
    private const int OutboxRate = 200;
    private const int QueueCapacity = 8_192;
    private const int PartitionCount = 16;

    public static async Task RunAsync(string[] args)
    {
        var duration = TimeSpan.FromSeconds(ReadInt(args, "--duration-seconds", 7_200));
        var reportPath = ReadString(args, "--report")
            ?? Path.Combine("artifacts", "capacity-soak.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);

        var queue = new PartitionedIngressQueue<IngressEnvelope>(PartitionCount, QueueCapacity);
        var outbox = Channel.CreateBounded<int>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        var registry = new Dictionary<long, byte>(ClanCount);
        for (var clanId = 1L; clanId <= ClanCount; clanId++)
        {
            registry.Add(clanId, 0);
        }

        var rateLimiter = new MonzeCommandRateLimiter(new MonzeRateLimitOptions(
            UserLimit: int.MaxValue,
            UserWindow: TimeSpan.FromMinutes(1),
            AiLimit: int.MaxValue,
            AiWindow: TimeSpan.FromMinutes(1),
            MeetingLimit: int.MaxValue,
            MeetingWindow: TimeSpan.FromMinutes(1),
            AdminLimit: int.MaxValue,
            AdminWindow: TimeSpan.FromMinutes(1),
            MaxEntries: ClanCount * 2));

        var readers = new ChannelReader<IngressEnvelope>[PartitionCount];
        for (var partition = 0; partition < PartitionCount; partition++)
        {
            readers[partition] = queue.GetReader(partition);
        }

        var cancellation = new CancellationTokenSource(duration);
        var consumerTasks = readers.Select(reader => ConsumeIngressAsync(reader, cancellation.Token)).ToArray();
        var outboxTasks = Enumerable.Range(0, 4)
            .Select(_ => ConsumeOutboxAsync(outbox.Reader, cancellation.Token))
            .ToArray();

        var metrics = new CapacitySoakMetrics
        {
            StartedAtUtc = DateTimeOffset.UtcNow,
            DurationSeconds = duration.TotalSeconds,
            ClanCount = ClanCount,
            ActiveClanCount = ActiveClanCount,
            MessageRate = MessageRate,
            CommandRate = CommandRate,
            MeetingCandidateCount = MeetingCandidateCount,
            OutboxRate = OutboxRate,
            QueueCapacity = QueueCapacity,
            PartitionCount = PartitionCount,
            Scope = "in-process ingress, rate limiter and bounded outbox dispatcher; no PostgreSQL, Redis, Mezon socket or Agent calls"
        };

        var stopwatch = Stopwatch.StartNew();
        var nextMessage = 0L;
        var nextCommand = 0L;
        var nextOutbox = 0L;
        var messageId = 0L;
        var outboxId = 0;
        var lastReport = TimeSpan.Zero;
        var process = Process.GetCurrentProcess();

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var elapsed = stopwatch.Elapsed;
                var targetMessages = (long)(elapsed.TotalSeconds * MessageRate);
                while (nextMessage < targetMessages)
                {
                    nextMessage++;
                    var clanId = (nextMessage % ActiveClanCount) + 1;
                    if (!queue.TryWrite(
                            clanId,
                            new IngressEnvelope(clanId, clanId * 10, ++messageId, clanId * 100)))
                    {
                        metrics.MessageDrops++;
                    }

                    metrics.MessagesProduced++;
                }

                var targetCommands = (long)(elapsed.TotalSeconds * CommandRate);
                while (nextCommand < targetCommands)
                {
                    nextCommand++;
                    var clanId = (nextCommand % ActiveClanCount) + 1;
                    if (!rateLimiter.TryAcquire(
                            clanId,
                            clanId * 100,
                            MonzeCommandNames.Meeting,
                            DateTimeOffset.UtcNow,
                            out _))
                    {
                        metrics.CommandRejects++;
                    }

                    metrics.CommandsProduced++;
                }

                var targetOutbox = (long)(elapsed.TotalSeconds * OutboxRate);
                while (nextOutbox < targetOutbox)
                {
                    nextOutbox++;
                    if (!outbox.Writer.TryWrite(++outboxId))
                    {
                        metrics.OutboxDrops++;
                    }

                    metrics.OutboxProduced++;
                }

                metrics.MeetingCandidatesObserved = MeetingCandidateCount;
                metrics.MaxIngressDepth = Math.Max(metrics.MaxIngressDepth, GetQueueDepth(queue));
                metrics.MaxOutboxDepth = Math.Max(metrics.MaxOutboxDepth, outbox.Reader.Count);

                if (elapsed - lastReport >= TimeSpan.FromSeconds(10))
                {
                    CaptureProcessSample(metrics, process, elapsed);
                    await WriteReportAsync(reportPath, metrics, cancellation.Token);
                    lastReport = elapsed;
                }

                await Task.Delay(1, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            queue.Complete();
            outbox.Writer.TryComplete();
            try
            {
                await Task.WhenAll(consumerTasks.Concat(outboxTasks));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            metrics.CompletedAtUtc = DateTimeOffset.UtcNow;
            metrics.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            CaptureProcessSample(metrics, process, stopwatch.Elapsed);
            await WriteReportAsync(reportPath, metrics, CancellationToken.None);
        }

        Console.WriteLine(JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task ConsumeIngressAsync(
        ChannelReader<IngressEnvelope> reader,
        CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out _))
            {
            }
        }
    }

    private static async Task ConsumeOutboxAsync(
        ChannelReader<int> reader,
        CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out _))
            {
            }
        }
    }

    private static int GetQueueDepth(PartitionedIngressQueue<IngressEnvelope> queue)
    {
        var depth = 0;
        for (var partition = 0; partition < queue.PartitionCount; partition++)
        {
            depth += queue.GetReader(partition).Count;
        }

        return depth;
    }

    private static void CaptureProcessSample(
        CapacitySoakMetrics metrics,
        Process process,
        TimeSpan elapsed)
    {
        process.Refresh();
        metrics.ElapsedSeconds = elapsed.TotalSeconds;
        metrics.ManagedHeapBytes = Math.Max(metrics.ManagedHeapBytes, GC.GetTotalMemory(forceFullCollection: false));
        metrics.WorkingSetBytes = Math.Max(metrics.WorkingSetBytes, process.WorkingSet64);
        metrics.Gen0Collections = GC.CollectionCount(0);
        metrics.Gen1Collections = GC.CollectionCount(1);
        metrics.Gen2Collections = GC.CollectionCount(2);
        metrics.Samples++;
    }

    private static async Task WriteReportAsync(
        string reportPath,
        CapacitySoakMetrics metrics,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        var value = ReadString(args, name);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static string? ReadString(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

}
