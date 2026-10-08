using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monze.Campaign.Load;
using Monze.Hosting;
using Monze.Simulator;
using Monze.Testing.Harness;
using Npgsql;

namespace Monze.Campaign.Chaos;

/// <summary>
/// Runs one <see cref="ChaosScenario"/>: Monze on a fresh database (through
/// the fault proxies) and the simulator, half the documented workload as
/// background load, then warm-up, fault, heal, observation and a drain of up
/// to 90 s (an outbox lease is 60 s). It measures the recovery time (RTO:
/// from heal until every command for 5 s is answered within 2 s) and checks
/// that nothing is lost or duplicated, Monze still answers, leases and
/// gauges settle, the heap stays bounded and the database invariants hold.
/// </summary>
public static class ChaosRun
{
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RecoveredWithin = TimeSpan.FromSeconds(30);
    private static readonly LoadWorkload Background = new(Warmup, TimeSpan.Zero, TimeSpan.Zero, 100, 10, 1, 0.5, 2.5, 100);

    public static async Task<CampaignArtifact> RunAsync(ChaosEnvironment environment, ChaosScenario scenario, int seed)
    {
        var artifact = new CampaignArtifact("chaos", scenario.Id, $"{scenario.Group}: {scenario.Title}");
        if (scenario.Blocked is { } reason)
        {
            return artifact.Block(reason);
        }

        artifact.Note($"Fault giữ {scenario.Hold.TotalSeconds:0} s, quan sát {scenario.Observe.TotalSeconds:0} s sau khi gỡ; tải nền 50 % workload tài liệu");
        if (scenario.KnownDefect is { } defect)
        {
            foreach (var invariant in scenario.KnownGapInvariants)
            {
                artifact.KnownGap(defect, invariant);
            }
        }

        await environment.HealAsync();
        var clock = new OffsetTimeProvider();
        var context = new ChaosContext(environment, clock);
        var world = LoadWorld.Create(10);
        var timeline = new List<(long Due, TimeSpan? Latency)>();
        if (scenario.BeforeStart is { } beforeStart)
        {
            await beforeStart(context);
        }

        var logs = new HostLogSink(LogLevel.Warning, capacity: 20_000);
        var startWatch = Stopwatch.StartNew();
        await using var host = await SimulatedMonzeHost.StartAsync(world.World, environment.Server, $"chaos_{scenario.Id.Replace('-', '_')}", new SimulatedMonzeHostOptions
        {
            Logs = logs,
            SeedAsync = world.SeedAsync,
            StartTimeout = TimeSpan.FromMinutes(3),
            MonzeConnectionString = environment.Proxied,
            Configuration = new Dictionary<string, string?> { ["Monze:Redis"] = scenario.UsesRedis ? environment.ProxiedRedis : null },
            Services = services => services.AddSingleton<TimeProvider>(clock),
            Timings = timings => timings with
            {
                SchedulerInterval = TimeSpan.FromSeconds(1),
                MaintenanceInterval = TimeSpan.FromSeconds(10),
                OutboxPollInterval = TimeSpan.FromMilliseconds(250),
                RoleScanInterval = TimeSpan.FromSeconds(300),
                MessageGapRetryBase = TimeSpan.FromMilliseconds(100),
                AgentScopeRetryBase = TimeSpan.FromMilliseconds(250),
                UncertainMarkTimeout = TimeSpan.FromSeconds(5)
            }
        });
        startWatch.Stop();
        context.Host = host;
        host.Recorder.RetainLimit = 5_000;
        using var collector = new MetricCollector("Monze", "Monze.Cache", "System.Runtime");

        var start = Stopwatch.GetTimestamp();
        var end = start + Ticks(Warmup + scenario.Hold + scenario.Observe);
        var driver = LoadDriver.Start(host, world, Background, seed, start, start, end);
        driver.Tracker.CommandCompleted = (due, latency) =>
        {
            lock (timeline)
            {
                timeline.Add((due, latency));
            }
        };
        await Task.Delay(Warmup);
        var heapBefore = SettledHeap();

        var faultStart = Stopwatch.GetTimestamp();
        var beforeFault = collector.Snapshot();
        await scenario.Inject(context);
        await Task.Delay(scenario.Hold);
        var healAt = Stopwatch.GetTimestamp();
        var fault = collector.Snapshot().Since(beforeFault);
        var redisDuringFault = fault.Sum("monze.cache.l2.hit") + fault.Sum("monze.cache.l2.miss") + fault.Sum("monze.cache.redis.error");
        if (scenario.Heal is { } heal)
        {
            await heal(context);
        }
        else
        {
            host.Simulator.Faults.Clear();
            await environment.HealAsync();
        }

        driver.WaitForSchedule();
        await driver.StopAsync();
        var drainUntil = Stopwatch.GetTimestamp() + Ticks(TimeSpan.FromSeconds(90));
        while (Stopwatch.GetTimestamp() < drainUntil && driver.Tracker.Unanswered().Outbox > 0)
        {
            driver.Tracker.Sweep();
            await Task.Delay(250);
        }

        driver.Tracker.Sweep();
        var stillServing = await LoadProbe.AnswersAsync(host, world, TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromSeconds(1));
        var settled = collector.Snapshot();
        var heapAfter = SettledHeap();
        var unanswered = driver.Tracker.Unanswered();
        IReadOnlyList<(long Due, TimeSpan? Latency)> points;
        lock (timeline)
        {
            points = timeline.OrderBy(static point => point.Due).ToList();
        }

        var rto = RecoveryTime(points, healAt);
        var recoveredAt = rto is { } recovery ? healAt + Ticks(recovery) : long.MaxValue;
        var lostBefore = points.Count(point => point.Due < faultStart && point.Latency is null);
        var lostDuring = points.Count(point => point.Due >= faultStart && point.Due < healAt && point.Latency is null);
        var lostAfter = points.Count(point => point.Due >= recoveredAt && point.Latency is null);
        var answeredDuring = points.Count(point => point.Due >= faultStart && point.Due < healAt && point.Latency is not null);
        var duringFault = new LatencyHistogram();
        foreach (var point in points.Where(point => point.Due >= faultStart && point.Due < healAt && point.Latency is not null))
        {
            duringFault.Record(point.Latency!.Value);
        }

        var heapGrowthMiB = (heapAfter - heapBefore) / (1024.0 * 1024.0);
        var inflight = settled.Sum("monze.command.inflight") + settled.Sum("monze.outbox.inflight");
        var critical = logs.CountAtLeast(LogLevel.Critical);
        IReadOnlyList<InvariantViolation> violations;
        var outboxStatuses = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var dataSource = NpgsqlDataSource.Create(host.Database.ConnectionString))
        {
            violations = await MonzeInvariants.CheckAsync(dataSource, TimeSpan.FromMinutes(1));
            await using var command = dataSource.CreateCommand("SELECT status, count(*) FROM outbox_delivery WHERE dedupe_key LIKE 'load-outbox:%' GROUP BY status;");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                outboxStatuses[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        artifact.Metric("rtoMs", rto is { } value ? value.TotalMilliseconds : -1)
            .Metric("lostDuringFault", lostDuring)
            .Metric("answeredDuringFault", answeredDuring)
            .Metric("duringFaultP95Ms", duringFault.ValueAtPercentile(95) / 1000.0)
            .Metric("lostAfterRecovery", lostAfter)
            .Metric("outboxDuplicates", driver.Tracker.OutboxDuplicates)
            .Metric("outboxUndelivered", unanswered.Outbox)
            .Metric("outboxUncertain", outboxStatuses.GetValueOrDefault("uncertain"))
            .Metric("outboxPending", outboxStatuses.GetValueOrDefault("pending") + outboxStatuses.GetValueOrDefault("sending"))
            .Metric("lostBeforeFault", lostBefore)
            .Metric("commands", points.Count)
            .Metric("heapGrowthMiB", heapGrowthMiB)
            .Metric("errorsLogged", logs.CountAtLeast(LogLevel.Error))
            .Metric("warningsLogged", logs.CountAtLeast(LogLevel.Warning))
            .Metric("timeToReadyMs", startWatch.Elapsed.TotalMilliseconds)
            .Metric("socketReconnects", settled.Sum("monze.socket.reconnect"))
            .Metric("generatorErrors", driver.Errors.Count)
            .Metric("redisDuringFault", redisDuringFault)
            .Metric("redisErrors", settled.Sum("monze.cache.redis.error"))
            .Invariant("recovered", $"phục hồi (mọi lệnh trong 5 s được trả lời ≤ 2 s) trong {RecoveredWithin.TotalSeconds:0} s sau khi gỡ fault", rto is { } r ? string.Create(CultureInfo.InvariantCulture, $"RTO {r.TotalSeconds:0.0} s") : "không phục hồi trong cửa sổ quan sát", rto is { } within && within <= RecoveredWithin)
            .Invariant("served-after-recovery", "không mất lệnh nào sau khi phục hồi", $"{lostAfter:N0} mất", rto is not null && lostAfter == 0)
            .Invariant("outbox-exactly-once", "mỗi dòng outbox gửi đúng một lần (sau drain ≤ 90 s)", $"{unanswered.Outbox:N0} chưa gửi, {driver.Tracker.OutboxDuplicates:N0} trùng / {driver.Tracker.OutboxInserted:N0}", unanswered.Outbox == 0 && driver.Tracker.OutboxDuplicates == 0)
            .Invariant("still-serving", "sau drain Monze trả lời lệnh thăm dò trong 10 s", stillServing ? "có" : "không", stillServing)
            .Invariant("settled", "command/outbox inflight về 0", inflight.ToString("0", CultureInfo.InvariantCulture), inflight == 0)
            .Invariant("bounded-memory", "heap sau GC tăng ≤ 64 MiB", string.Create(CultureInfo.InvariantCulture, $"{heapGrowthMiB:0.0} MiB"), heapGrowthMiB <= 64)
            .Invariant("no-critical", "không có log Critical", $"{critical:N0}", critical == 0)
            .DbInvariants(violations);
        if (scenario.ServedDuringFault)
        {
            artifact.Invariant("served-during-fault", "lệnh vẫn được trả lời khi fault đang giữ (phụ thuộc tuỳ chọn)", $"{lostDuring:N0} mất, {answeredDuring:N0} trả lời", lostDuring == 0 && answeredDuring > 0)
                .P99AtMost("fast-during-fault", duringFault, 1_000, "phụ thuộc tuỳ chọn không được làm lệnh chậm quá SLO lệnh p99 ≤ 1 s");
        }

        if (scenario.UsesRedis && redisDuringFault == 0)
        {
            artifact.Block("Không có lượt đọc Redis nào trong lúc fault (Redis chỉ cache cài đặt welcome, L1 trả lời): kịch bản không chứng minh được hành vi khi Redis lỗi.");
        }

        foreach (var entry in logs.Problems.Where(static entry => entry.Level >= LogLevel.Error).Select(static entry => $"{entry.Level} {entry.Category}").Distinct().Take(5))
        {
            artifact.Note("log: " + entry);
        }

        return artifact;
    }

    /// <summary>
    /// The first time after <paramref name="healAt"/> from which every
    /// command due in the next 5 s (at least one) was answered within 2 s.
    /// </summary>
    private static TimeSpan? RecoveryTime(IReadOnlyList<(long Due, TimeSpan? Latency)> points, long healAt)
    {
        var window = Ticks(TimeSpan.FromSeconds(5));
        var after = points.Where(point => point.Due >= healAt).ToList();
        for (var i = 0; i < after.Count; i++)
        {
            var from = after[i].Due;
            var slice = after.Skip(i).TakeWhile(point => point.Due < from + window).ToList();
            if (slice.Count > 0
                && slice.All(static point => point.Latency is { } latency && latency <= TimeSpan.FromSeconds(2))
                && after[^1].Due >= from + window)
            {
                return TimeSpan.FromTicks((from - healAt) * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
            }
        }

        return null;
    }

    private static long SettledHeap()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetGCMemoryInfo().HeapSizeBytes;
    }

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);
}
