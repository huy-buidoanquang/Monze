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
/// HTTP scenarios add the Agent/AI traffic (<see cref="AgentAiTraffic"/>) and
/// check that meetings are summarized once and recover after the fault, no
/// session stays live, AI commands are answered and the AI budget matches
/// the answers; Redis scenarios add welcome edits and joins
/// (<see cref="RedisWelcomeTraffic"/>) and check that welcomes stay fresh.
/// </summary>
public static class ChaosRun
{
    /// <summary>The AI key the HTTP fake accepts; a fixed test value, not a secret.</summary>
    private const string AiKey = "chaos-fake-ai-key-not-a-secret";
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
        if (scenario.UsesHttp)
        {
            artifact.Note("Agent SSE, transcript và AI qua SimHttpHost; tải nền thêm chu kỳ meeting Agent và lệnh *ai translate trên 2 clan Agent");
        }

        if (scenario.KnownDefect is { } defect)
        {
            foreach (var invariant in scenario.KnownGapInvariants)
            {
                artifact.KnownGap(defect, invariant);
            }
        }

        foreach (var (otherDefect, invariant) in scenario.OtherKnownGaps)
        {
            artifact.KnownGap(otherDefect, invariant);
        }

        await environment.HealAsync();
        var clock = new OffsetTimeProvider();
        var context = new ChaosContext(environment, clock);
        var world = LoadWorld.Create(10);
        context.World = world;
        var timeline = new List<(long Due, TimeSpan? Latency)>();
        IReadOnlyList<AgentClan> agentClans = [];
        await using var http = scenario.UsesHttp
            ? await SimHttpHost.StartAsync(new SimHttpHostOptions { BotId = world.BotId, BotToken = world.World.Bot.Token, AiApiKey = AiKey })
            : null;
        if (http is not null)
        {
            agentClans = AgentClan.AddTo(world.World);
            context.Http = http;
        }

        if (scenario.BeforeStart is { } beforeStart)
        {
            await beforeStart(context);
        }

        var logs = new HostLogSink(LogLevel.Warning, capacity: 20_000);
        context.AddSink(logs);
        var configuration = new Dictionary<string, string?> { ["Monze:Redis"] = scenario.UsesRedis ? environment.ProxiedRedis : null };
        if (http is not null)
        {
            configuration["Mezon:AgentBaseUrl"] = http.BaseUrl;
            configuration["Monze:Ai:BaseUrl"] = http.BaseUrl;
            configuration["Monze:Ai:ApiKey"] = AiKey;

            // The AI traffic spreads over 20 members; the per-user AI bucket (3/min) would answer most of it with "rate limited".
            configuration["Monze:RateLimit:AiLimit"] = "1000";
        }

        var options = new SimulatedMonzeHostOptions
        {
            Logs = logs,
            SeedAsync = world.SeedAsync,
            StartTimeout = TimeSpan.FromMinutes(3),
            MonzeConnectionString = environment.Proxied,
            Configuration = configuration,
            Services = services => services.AddSingleton<TimeProvider>(clock),
            Timings = timings =>
            {
                var chaos = timings with
                {
                    SchedulerInterval = TimeSpan.FromSeconds(1),
                    MaintenanceInterval = TimeSpan.FromSeconds(10),
                    OutboxPollInterval = TimeSpan.FromMilliseconds(250),
                    RoleScanInterval = TimeSpan.FromSeconds(300),
                    MessageGapRetryBase = TimeSpan.FromMilliseconds(100),
                    AgentScopeRetryBase = TimeSpan.FromMilliseconds(250),
                    UncertainMarkTimeout = TimeSpan.FromSeconds(5)
                };
                return scenario.Timings?.Invoke(chaos) ?? chaos;
            }
        };
        context.HostOptions = options;
        var startWatch = Stopwatch.StartNew();
        await using var host = await SimulatedMonzeHost.StartAsync(world.World, environment.Server, $"chaos_{scenario.Id.Replace('-', '_')}", options);
        startWatch.Stop();
        context.Host = host;
        host.Recorder.RetainLimit = 5_000;
        try
        {
            return await RunStartedAsync(context, scenario, artifact, world, host, http, agentClans, timeline, startWatch.Elapsed, seed);
        }
        finally
        {
            if (context.Agent is { } agent)
            {
                await agent.StopAsync();
            }

            if (context.Redis is { } redis)
            {
                await redis.StopAsync();
            }

            await context.DisposeInstancesAsync();
        }
    }

    private static async Task<CampaignArtifact> RunStartedAsync(
        ChaosContext context,
        ChaosScenario scenario,
        CampaignArtifact artifact,
        LoadWorld world,
        SimulatedMonzeHost host,
        SimHttpHost? http,
        IReadOnlyList<AgentClan> agentClans,
        List<(long Due, TimeSpan? Latency)> timeline,
        TimeSpan timeToReady,
        int seed)
    {
        var environment = context.Environment;
        using var collector = new MetricCollector("Monze", "Monze.Cache", "System.Runtime");
        if (http is not null)
        {
            await http.WaitForSseAsync(1, TimeSpan.FromSeconds(30));
            var agent = new AgentAiTraffic(http, context, agentClans, host.Database.ConnectionString, seed);
            host.Recorder.Recorded += agent.OnRecorded;
            context.Agent = agent;
            agent.Start();
        }

        if (scenario.UsesRedis)
        {
            var redis = new RedisWelcomeTraffic(context, world);
            host.Recorder.Recorded += redis.OnRecorded;
            context.Redis = redis;
            redis.Start();
        }

        using var agentDepth = new GaugeSampler(collector, "monze.ingress.agent.depth", "monze.ingress.agent.pending_writers");
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
        context.Redis?.SetFaultWindow(faultStart, long.MaxValue);
        var beforeFault = collector.Snapshot();
        await scenario.Inject(context);
        await Task.Delay(scenario.Hold);
        var healAt = Stopwatch.GetTimestamp();
        context.Redis?.SetFaultWindow(faultStart, healAt);
        var fault = collector.Snapshot().Since(beforeFault);
        var redisDuringFault = fault.Sum("monze.cache.l2.hit") + fault.Sum("monze.cache.l2.miss") + fault.Sum("monze.cache.redis.error");
        if (scenario.Heal is { } heal)
        {
            await heal(context);
        }
        else
        {
            await context.HealDefaultsAsync();
        }

        driver.WaitForSchedule();
        await driver.StopAsync();
        if (context.Agent is { } stopping)
        {
            await stopping.StopAsync();
        }

        if (context.Redis is { } redisTraffic)
        {
            await redisTraffic.StopAsync();
        }

        var drainUntil = Stopwatch.GetTimestamp() + Ticks(TimeSpan.FromSeconds(90));
        while (Stopwatch.GetTimestamp() < drainUntil && driver.Tracker.Unanswered().Outbox > 0)
        {
            driver.Tracker.Sweep();
            await Task.Delay(250);
        }

        if (context.Agent is { } draining)
        {
            await draining.DrainAsync(TimeSpan.FromSeconds(Math.Max(15, (drainUntil - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency)), scenario.WarpSummaryRetries);
        }

        driver.Tracker.Sweep();
        var current = context.RequireHost();
        var stillServing = await LoadProbe.AnswersAsync(current, world, TimeSpan.FromSeconds(10));
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
        var critical = context.LoggedAtLeast(LogLevel.Critical);
        IReadOnlyList<InvariantViolation> violations;
        var outboxStatuses = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var dataSource = NpgsqlDataSource.Create(host.Database.ConnectionString))
        {
            violations = await MonzeInvariants.CheckAsync(dataSource, TimeSpan.FromMinutes(1), http is not null ? 2_000 : null);
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
            .Metric("extraOutputs", driver.Tracker.ExtraOutputs)
            .Metric("heapGrowthMiB", heapGrowthMiB)
            .Metric("errorsLogged", context.LoggedAtLeast(LogLevel.Error))
            .Metric("warningsLogged", context.LoggedAtLeast(LogLevel.Warning))
            .Metric("timeToReadyMs", timeToReady.TotalMilliseconds)
            .Metric("socketReconnects", settled.Sum("monze.socket.reconnect"))
            .Metric("generatorErrors", driver.Errors.Count)
            .Metric("redisDuringFault", redisDuringFault)
            .Metric("redisErrors", settled.Sum("monze.cache.redis.error"))
            .Metric("instances", 1 + context.Instances.Count)
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

        foreach (var point in points.Where(point => point.Due >= faultStart && point.Latency is null).Take(3))
        {
            artifact.Note(string.Create(CultureInfo.InvariantCulture, $"lệnh mất: đến hạn {(point.Due - faultStart) / (double)Stopwatch.Frequency:0.0} s sau khi bắt đầu fault (gỡ fault ở {(healAt - faultStart) / (double)Stopwatch.Frequency:0.0} s)"));
        }

        if (scenario.ChecksSingleAnswer)
        {
            artifact.Invariant("answered-once", "mỗi lệnh được trả lời đúng một lần dù nhiều instance nhận cùng tin", $"{driver.Tracker.ExtraOutputs:N0} câu trả lời thừa", driver.Tracker.ExtraOutputs == 0);
        }

        if (context.Agent is { } traffic)
        {
            await AddAgentAiAsync(artifact, traffic, http!, agentDepth, settled, healAt);
        }

        if (context.Redis is { } welcomes)
        {
            AddRedis(artifact, welcomes, scenario);
        }

        if (scenario.Verify is { } verify)
        {
            await verify(context, artifact);
        }

        // A paused Redis neither answers nor fails within the fault (RedisTimeoutException is not
        // counted, DEF-01), so a run whose welcome traffic read the settings during the fault
        // exercised Redis even when the cache counters stayed at zero.
        if (scenario.UsesRedis && redisDuringFault == 0 && (context.Redis?.JoinsDuringFault ?? 0) == 0)
        {
            artifact.Block("Không có lượt đọc Redis nào trong lúc fault (Redis chỉ cache cài đặt welcome, L1 trả lời): kịch bản không chứng minh được hành vi khi Redis lỗi.");
        }

        foreach (var entry in context.Sinks.SelectMany(static sink => sink.Problems).Where(static entry => entry.Level >= LogLevel.Error).Select(static entry => $"{entry.Level} {entry.Category}").Distinct().Take(5))
        {
            artifact.Note("log: " + entry);
        }

        return artifact;
    }

    private static async Task AddAgentAiAsync(CampaignArtifact artifact, AgentAiTraffic traffic, SimHttpHost http, GaugeSampler agentDepth, MetricSnapshot settled, long healAt)
    {
        // Cycles that started once the stream had time to come back (the SDK reconnects 3–4 s after a drop).
        var report = await traffic.MeasureAsync(healAt + Ticks(TimeSpan.FromSeconds(10)), Stopwatch.GetTimestamp() - Ticks(TimeSpan.FromSeconds(40)));
        var requests = http.Requests;
        int Count(SimHttpRoute route, Func<int, bool> status) => requests.Count(request => request.Route == route && status(request.Status));
        artifact.Metric("agentCycles", report.Cycles)
            .Metric("agentPosted", report.Posted)
            .Metric("agentFailed", report.Failed)
            .Metric("agentStuck", report.Stuck)
            .Metric("agentStuckClean", report.StuckClean)
            .Metric("agentDeferred", report.Deferred)
            .Metric("agentMissing", report.Missing)
            .Metric("agentLostUpstream", report.Lost)
            .Metric("agentAfterRecovery", report.AfterRecovery)
            .Metric("agentBindP99Ms", traffic.BindLatency.ValueAtPercentile(99) / 1000.0)
            .Metric("summaryDuplicates", report.SummaryDuplicates)
            .Metric("summaryMessages", report.SummaryMessages)
            .Metric("failureNotices", report.FailureNotices)
            .Metric("sseConnections", http.SseConnections)
            .Metric("agentIngressMaxDepth", agentDepth.Max("monze.ingress.agent.depth"))
            .Metric("agentPendingWritersMax", agentDepth.Max("monze.ingress.agent.pending_writers"))
            .Metric("agentEventsIgnored", settled.Sum("monze.agent.events.ignored"))
            .Metric("transcriptOk", Count(SimHttpRoute.TranscriptSummary, static status => status == 200))
            .Metric("transcriptFailed", Count(SimHttpRoute.TranscriptSummary, static status => status != 200))
            .Metric("transcriptRefresh", Count(SimHttpRoute.TranscriptRefresh, static status => status == 200))
            .Metric("aiSent", report.AiSent)
            .Metric("aiAnswered", report.AiAnswered)
            .Metric("aiProviderEmpty", report.AiProviderEmpty)
            .Metric("aiBusy", report.AiBusy)
            .Metric("aiBudgetExceeded", report.AiBudgetExceeded)
            .Metric("aiRateLimited", report.AiRateLimited)
            .Metric("aiOther", report.AiOther)
            .Metric("aiUnanswered", report.AiUnanswered)
            .Metric("aiP99Ms", traffic.AiLatency.ValueAtPercentile(99) / 1000.0)
            .Metric("aiTokensExpected", report.ExpectedTokens)
            .Metric("aiTokens", report.ActualTokens)
            .Metric("httpTrafficErrors", traffic.Errors.Count)
            .Invariant("agent-recovered", "chu kỳ Agent bắt đầu ≥ 10 s sau khi gỡ fault đều được tóm tắt", $"{report.AfterRecovery - report.AfterRecoveryNotPosted}/{report.AfterRecovery} posted", report.AfterRecovery > 0 && report.AfterRecoveryNotPosted == 0)
            .Invariant("meetings-summarized", "mọi chu kỳ meeting kết thúc posted, summary_failed có thông báo, hoặc còn lịch retry (không kẹt live, không mất phiên)", $"{report.Stuck} kẹt, {report.Missing} không có phiên ({report.StuckClean} kẹt dù mọi event đã tới Monze), {report.Lost} chu kỳ mất event ở upstream", report.Stuck + report.Missing == 0)
            .Invariant(
                "agent-events-timely",
                "room_started tới stream đang mở được áp dụng (phiên live) p99 ≤ 5 s (đề xuất; một lần chờ bị cắt ở 10 s)",
                string.Create(CultureInfo.InvariantCulture, $"p99 {traffic.BindLatency.ValueAtPercentile(99) / 1000.0:0} ms over {traffic.BindLatency.Count}"),
                traffic.BindLatency.Count > 0 && traffic.BindLatency.ValueAtPercentile(99) <= 5_000_000)
            .Invariant("summaries-once", "mỗi tóm tắt và danh sách đầu việc được gửi đúng một lần", $"{report.SummaryDuplicates} trùng", report.SummaryDuplicates == 0)
            .Invariant("failure-notice-once", "mỗi phiên summary_failed có đúng một thông báo lỗi", $"{report.FailureNotices} thông báo / {report.Failed} phiên", report.FailureNotices == report.Failed)
            .Invariant("ai-answered", "mọi lệnh AI (gửi trước 40 s cuối) được trả lời", $"{report.AiUnanswered} chưa trả lời / {report.AiSent}", report.AiUnanswered == 0)
            .Invariant("ai-recovered", "sau khi gỡ fault provider AI trả lời được", $"{report.AiAnsweredAfterRecovery} câu trả lời", report.AiAnsweredAfterRecovery > 0)
            .Invariant("ai-budget-consistent", "ai_usage = tổng token của các lệnh đã gọi provider", $"{report.ActualTokens} / {report.ExpectedTokens}", report.ActualTokens == report.ExpectedTokens);
        if (report.Burst > 0)
        {
            artifact.Metric("burstRooms", report.Burst)
                .Metric("burstPosted", report.BurstPosted)
                .Invariant("burst-posted", "mọi phòng trong đợt burst được tóm tắt", $"{report.BurstPosted}/{report.Burst}", report.BurstPosted == report.Burst);
        }

        if (report.Large > 0)
        {
            artifact.Metric("largeCycles", report.Large)
                .Metric("largePosted", report.LargePosted)
                .Invariant("large-summaries-posted", "cuộc họp có transcript lớn (600 KiB) vẫn được tóm tắt", $"{report.LargePosted}/{report.Large}", report.LargePosted == report.Large);
        }
    }

    private static void AddRedis(CampaignArtifact artifact, RedisWelcomeTraffic welcomes, ChaosScenario scenario)
    {
        var missing = welcomes.Missing(Stopwatch.GetTimestamp() - Ticks(TimeSpan.FromSeconds(15)));
        artifact.Metric("redisEdits", welcomes.Edits)
            .Metric("redisEditsAnswered", welcomes.EditsAnswered)
            .Metric("redisJoins", welcomes.Joins)
            .Metric("redisWelcomed", welcomes.Welcomed)
            .Metric("redisStaleWelcomes", welcomes.Stale)
            .Metric("redisWelcomeP95Ms", welcomes.Welcomes.ValueAtPercentile(95) / 1000.0)
            .Metric("redisWelcomeDuringFaultP95Ms", welcomes.WelcomesDuringFault.ValueAtPercentile(95) / 1000.0)
            .Metric("redisTrafficErrors", welcomes.Errors.Count)
            .Metric("redisJoinsDuringFault", welcomes.JoinsDuringFault)
            .Invariant("welcome-fresh", "mỗi lời chào sau khi sửa mang nội dung mới nhất (invalidation qua Redis)", $"{welcomes.Stale} lời chào cũ / {welcomes.Welcomed}", welcomes.Stale == 0)
            .Invariant("welcome-delivered", "mọi người mới (trước 15 s cuối) được chào", $"{missing} chưa được chào / {welcomes.Joins}", missing == 0);
        foreach (var example in welcomes.StaleExamples)
        {
            artifact.Note("lời chào cũ: " + example);
        }

        if (scenario.ServedDuringFault)
        {
            artifact.Invariant(
                "welcome-fast-during-fault",
                "lời chào khi Redis lỗi vẫn p95 ≤ 2 s (SLO welcome, Redis là tuỳ chọn)",
                string.Create(CultureInfo.InvariantCulture, $"p95 {welcomes.WelcomesDuringFault.ValueAtPercentile(95) / 1000.0:0} ms over {welcomes.WelcomesDuringFault.Count}"),
                welcomes.WelcomesDuringFault.Count > 0 && welcomes.WelcomesDuringFault.ValueAtPercentile(95) <= 2_000_000);
        }
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
