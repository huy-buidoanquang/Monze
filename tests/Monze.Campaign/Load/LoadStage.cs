using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Monze.Campaign.Component;
using Monze.Hosting;
using Monze.Simulator;
using Monze.Testing.Harness;
using Npgsql;

namespace Monze.Campaign.Load;

/// <summary>
/// One full-process load stage: the real Monze host on a fresh campaign
/// database and the offline simulator, with production worker timings,
/// driven open-loop by one generator thread (chat messages, commands, help
/// clicks, joins and voice changes) and an outbox writer that inserts due
/// announcements with a unique nonce. After a warm-up the stage measures
/// end-to-end latencies, Monze metrics, process metrics and database load,
/// then drains. The stage is INVALID-HARNESS (BLOCKED) when the generator
/// fell behind its schedule or ran out of free command channels.
/// </summary>
public static class LoadStage
{
    public const string OutboxNoncePrefix = "lo-";
    private static readonly string[] Meters = ["Monze", "Monze.Cache", "Npgsql", "System.Runtime"];

    public static async Task<CampaignArtifact> RunAsync(ComponentContext context, LoadVariant variant, int registered, LoadWorkload workload)
    {
        var id = $"L-{variant.Id}-S{registered}";
        var artifact = new CampaignArtifact("load", id, $"Load {variant.Id} ({variant.Description}), {registered:N0} clan đăng ký, {Math.Min(registered, LoadWorld.MaxActiveClans)} active");
        artifact.Note($"Workload: {workload.MessagesPerSecond} msg/s, {workload.CommandsPerSecond} lệnh/s, {workload.ClicksPerSecond} click/s, {workload.JoinsPerSecond} join/s, {workload.VoiceChangesPerSecond} voice/s, {workload.OutboxPerSecond} outbox/s; warm-up {workload.Warmup.TotalSeconds:0} s, đo {workload.Measure.TotalSeconds:0} s, drain {workload.Drain.TotalSeconds:0} s")
            .Note($"Tỉ lệ lệnh (giả định campaign): {LoadWorkload.DescribeMix()}");
        var world = LoadWorld.Create(registered);
        var latencyRandom = new Random(context.Seed);
        var simulatorOptions = new MezonSimulatorOptions
        {
            KeepClientRateLimits = variant.KeepClientRateLimits,
            ResponseLatency = variant.AckLatency ? _ => LogNormalAck(latencyRandom) : null
        };
        var logs = new HostLogSink(LogLevel.Warning, capacity: 20_000);
        var startWatch = Stopwatch.StartNew();
        await using var host = await SimulatedMonzeHost.StartAsync(world.World, context.Server, $"load_{variant.Id}_{registered}", new SimulatedMonzeHostOptions
        {
            Simulator = simulatorOptions,
            Logs = logs,
            SeedAsync = world.SeedAsync,
            StartTimeout = TimeSpan.FromMinutes(3),
            Timings = ProductionTimings
        });
        startWatch.Stop();
        host.Recorder.RetainLimit = 5_000;
        using var collector = new MetricCollector(Meters);
        var dbBefore = await DatabaseCountersAsync(host.Database.ConnectionString);

        var start = Stopwatch.GetTimestamp();
        var measureStart = start + Ticks(workload.Warmup);
        var measureEnd = measureStart + Ticks(workload.Measure);
        var driver = LoadDriver.Start(host, world, workload, context.Seed, start, measureStart, measureEnd);
        var tracker = driver.Tracker;
        var errors = driver.Errors;
        while (Stopwatch.GetTimestamp() < measureStart)
        {
            await Task.Delay(50);
        }

        var baseline = collector.Snapshot();
        var dbAtMeasure = await DatabaseCountersAsync(host.Database.ConnectionString);
        driver.WaitForSchedule();
        var measured = collector.Snapshot();
        var dbAtEnd = await DatabaseCountersAsync(host.Database.ConnectionString);
        await driver.StopAsync();

        // Drain: let in-flight work finish, then count what never came back.
        var drainUntil = Stopwatch.GetTimestamp() + Ticks(workload.Drain);
        while (Stopwatch.GetTimestamp() < drainUntil)
        {
            tracker.Sweep();
            await Task.Delay(100);
        }

        tracker.Sweep();
        var unanswered = tracker.Unanswered();
        var delta = measured.Since(baseline);
        var seconds = workload.Measure.TotalSeconds;
        var violations = await InvariantsAsync(host.Database.ConnectionString);
        var hostErrors = logs.CountAtLeast(LogLevel.Error);
        var unmodelled = host.Recorder.UnmodelledCalls.Count;
        var outboxLimitMs = variant.AckLatency ? 5_000 : 2_000;

        artifact.Metric("commandP95Ms", tracker.Commands.ValueAtPercentile(95) / 1000.0)
            .Metric("commandP99Ms", tracker.Commands.ValueAtPercentile(99) / 1000.0)
            .Metric("outboxP99Ms", tracker.Outbox.ValueAtPercentile(99) / 1000.0)
            .Metric("outboundWritesPerSecond", tracker.OutboundWrites / seconds)
            .Metric("apiReadsPerSecond", tracker.ApiReads / seconds)
            .Metric("commandsAnswered", tracker.CommandsAnswered)
            .Metric("commandsLost", tracker.CommandsLost)
            .Metric("timeToReadyMs", startWatch.Elapsed.TotalMilliseconds)
            .Latency("command", tracker.Commands)
            .Latency("click", tracker.Clicks)
            .Latency("welcome", tracker.Welcomes)
            .Latency("outbox", tracker.Outbox)
            .Latency("generatorLag", tracker.GeneratorLag)
            .Metric("commandsSent", tracker.CommandsSent)
            .Metric("commandsSkipped", tracker.CommandsSkipped)
            .Metric("extraOutputs", tracker.ExtraOutputs)
            .Metric("clicksSent", tracker.ClicksSent)
            .Metric("clicksSkipped", tracker.ClicksSkipped)
            .Metric("clicksUnanswered", unanswered.Clicks)
            .Metric("joinsSent", tracker.JoinsSent)
            .Metric("joinsUnwelcomed", unanswered.Joins)
            .Metric("outboxInserted", tracker.OutboxInserted)
            .Metric("outboxDuplicates", tracker.OutboxDuplicates)
            .Metric("outboxUndelivered", unanswered.Outbox)
            .Metric("generatorErrors", errors.Count)
            .Metric("ingressDropped", delta.Sum("monze.ingress.message.dropped") + delta.Sum("monze.ingress.message_gap.dropped"))
            .Metric("upstreamRateLimitDelayed", delta.Sum("monze.upstream.ratelimit.delayed"))
            .Metric("outboxDeliveredPerSecond", delta.Sum("monze.outbox.delivered") / seconds)
            .Metric("socketReconnects", delta.Sum("monze.socket.reconnect"))
            .Metric("gcCollections", measured.Sum("dotnet.gc.collections") - baseline.Sum("dotnet.gc.collections"))
            .Metric("allocatedMiBPerSecond", (measured.Sum("dotnet.gc.heap.total_allocated") - baseline.Sum("dotnet.gc.heap.total_allocated")) / seconds / (1024 * 1024))
            .Metric("cpuSecondsPerSecond", (measured.Sum("dotnet.process.cpu.time") - baseline.Sum("dotnet.process.cpu.time")) / seconds)
            .Metric("workingSetMiB", measured.Sum("dotnet.process.memory.working_set") / (1024 * 1024))
            .Metric("dbTransactionsPerSecond", (dbAtEnd.Commits - dbAtMeasure.Commits) / seconds)
            .Metric("dbRowsWrittenPerSecond", (dbAtEnd.RowsWritten - dbAtMeasure.RowsWritten) / seconds)
            .Metric("dbTransactionsTotal", dbAtEnd.Commits - dbBefore.Commits)
            .Metric("hostErrors", hostErrors);

        artifact.P99AtMost("command-p99", tracker.Commands, 1_000, "SLO đề xuất lệnh p99 ≤ 1 s")
            .Invariant("command-p95", "p95 ≤ 500 ms (SLO đề xuất)", Ms(tracker.Commands, 95), tracker.Commands.Count > 0 && tracker.Commands.ValueAtPercentile(95) <= 500_000)
            .P99AtMost("button-p99", tracker.Clicks, 1_000, "SLO đề xuất button p99 ≤ 1 s")
            .Invariant("welcome-p95", "p95 ≤ 2 s (SLO đề xuất)", Ms(tracker.Welcomes, 95), tracker.Welcomes.Count > 0 && tracker.Welcomes.ValueAtPercentile(95) <= 2_000_000)
            .P99AtMost("outbox-p99", tracker.Outbox, outboxLimitMs, $"SLO đề xuất outbox p99 ≤ {outboxLimitMs / 1000} s")
            .Invariant(
                "commands-answered",
                "mọi lệnh trong cửa sổ đo được trả lời trong 10 s",
                $"{tracker.CommandsLost:N0} mất / {tracker.CommandsSent:N0}, {tracker.CommandsSkipped:N0} không gửi được vì mọi kênh lệnh còn chờ trả lời",
                tracker.CommandsLost == 0 && tracker.CommandsSkipped == 0 && tracker.CommandsSent > 0)
            .Invariant("interactions-answered", "mọi click và join trong cửa sổ đo có phản hồi", $"{unanswered.Clicks:N0} click, {unanswered.Joins:N0} join không phản hồi", unanswered.Clicks == 0 && unanswered.Joins == 0)
            .Invariant("outbox-exactly-once", "mỗi dòng outbox gửi đúng một lần", $"{unanswered.Outbox:N0} chưa gửi, {tracker.OutboxDuplicates:N0} trùng / {tracker.OutboxInserted:N0}", unanswered.Outbox == 0 && tracker.OutboxDuplicates == 0 && tracker.OutboxInserted > 0)
            .Invariant("no-drops", "không rơi sự kiện ingress (tài liệu)", $"{delta.Sum("monze.ingress.message.dropped") + delta.Sum("monze.ingress.message_gap.dropped"):0}", delta.Sum("monze.ingress.message.dropped") + delta.Sum("monze.ingress.message_gap.dropped") == 0)
            .Invariant("host-clean", "không có log Error, không gọi API chưa mô hình hoá", $"{hostErrors:N0} log lỗi, {unmodelled:N0} gọi lạ", hostErrors == 0 && unmodelled == 0)
            .DbInvariants(violations);
        if (variant.AckLatency)
        {
            // CAND-20: with a 30 ms median ack the outbox delivers ~150/s, below the documented 200/s.
            artifact.KnownGap("CAND-20", "outbox-p99").KnownGap("CAND-20", "outbox-exactly-once");
        }

        if (variant == LoadVariant.V1)
        {
            foreach (var invariant in new[] { "command-p99", "command-p95", "button-p99", "welcome-p95", "outbox-p99", "commands-answered", "interactions-answered", "outbox-exactly-once" })
            {
                artifact.KnownGap("DEF-03", invariant);
            }
        }

        var lagP99 = tracker.GeneratorLag.ValueAtPercentile(99);
        if (lagP99 > 10_000)
        {
            artifact.Block($"INVALID-HARNESS: generator lag p99 {lagP99 / 1000.0:0.##} ms > 10 ms");
        }

        if (errors.Count > 0)
        {
            artifact.Note($"Lỗi generator: {errors.Count:N0} ({string.Join(", ", errors.Types)})");
        }

        foreach (var line in logs.Problems.Take(5))
        {
            artifact.Note($"log: {line.Level} {line.Category}");
        }

        return artifact;
    }

    private static MonzeWorkerTimings ProductionTimings(MonzeWorkerTimings shortened)
        => shortened with
        {
            SchedulerInterval = TimeSpan.FromSeconds(1),
            MaintenanceInterval = TimeSpan.FromMinutes(1),
            OutboxPollInterval = TimeSpan.FromMilliseconds(250),
            RoleScanInterval = TimeSpan.FromSeconds(300),
            MessageGapRetryBase = TimeSpan.FromMilliseconds(100),
            AgentScopeRetryBase = TimeSpan.FromMilliseconds(250),
            UncertainMarkTimeout = TimeSpan.FromSeconds(5)
        };

    /// <summary>Log-normal with median 30 ms and p99 150 ms (sigma = ln 5 / 2.326).</summary>
    private static TimeSpan LogNormalAck(Random random)
    {
        double normal;
        lock (random)
        {
            normal = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        }

        return TimeSpan.FromMilliseconds(Math.Exp(Math.Log(30) + Math.Log(5) / 2.326 * normal));
    }

    private static async Task<(long Commits, long RowsWritten)> DatabaseCountersAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT xact_commit, tup_inserted + tup_updated + tup_deleted FROM pg_stat_database WHERE datname = current_database();",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<IReadOnlyList<InvariantViolation>> InvariantsAsync(string connectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        return await MonzeInvariants.CheckAsync(dataSource, TimeSpan.FromMinutes(1));
    }

    private static string Ms(LatencyHistogram histogram, double percentile)
        => string.Create(CultureInfo.InvariantCulture, $"{histogram.ValueAtPercentile(percentile) / 1000.0:0.###} ms over {histogram.Count:N0}");

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);
}
