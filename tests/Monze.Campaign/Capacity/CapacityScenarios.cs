using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Monze.Campaign.Component;
using Monze.Campaign.Load;
using Monze.Hosting;
using Monze.Simulator;
using Monze.Testing.Harness;
using Npgsql;

namespace Monze.Campaign.Capacity;

/// <summary>
/// Backpressure and capacity scenarios on the real host and the simulator:
/// - BP-1: a message burst into one ingress lane;
/// - BP-4: a join storm through the welcome queue;
/// - BP-5: a command storm at V1 and V3;
/// - BP-6: an outbox backlog;
/// - BP-7: a rejoin storm with 1,000 registered clans;
/// - BP-8: the discovery boundary (99 vs 100 visible clans, and DEF-06 with
///   150 member clans).
/// BP-2 (gap queue) and BP-3 (Agent pending writers) need hundreds of
/// dropping channels and the Agent SSE fake, so they are recorded as
/// BLOCKED.
/// </summary>
public static class CapacityScenarios
{
    private const int ConfiguredMessageCapacity = 8192;
    private static readonly string[] Meters = ["Monze", "System.Runtime"];

    public static IReadOnlyList<(string Id, Func<ComponentContext, Task<CampaignArtifact>> Run)> All { get; } =
    [
        ("BP-1", LaneOverflowAsync),
        ("BP-2", static _ => Task.FromResult(new CampaignArtifact("capacity", "BP-2", "Tràn hàng đợi gap").Block("Cần hàng trăm channel cùng rơi tin; chưa có trong revision này."))),
        ("BP-3", static _ => Task.FromResult(new CampaignArtifact("capacity", "BP-3", "Pending writer của hàng đợi Agent").Block("Cần fake Agent SSE (SimHttpHost)."))),
        ("BP-4", WelcomeStormAsync),
        ("BP-5", static context => CommandStormAsync(context, LoadVariant.V3)),
        ("BP-5-V1", static context => CommandStormAsync(context, LoadVariant.V1)),
        ("BP-6", OutboxBacklogAsync),
        ("BP-7", RejoinStormAsync),
        ("BP-8", DiscoveryBoundaryAsync),
        ("BP-8-DEF06", DiscoveryCapAsync)
    ];

    private static async Task<CampaignArtifact> LaneOverflowAsync(ComponentContext context)
    {
        var total = context.Full ? 50_000 : 20_000;
        var artifact = new CampaignArtifact("capacity", "BP-1", $"Burst {total:N0} tin vào một lane ingress (capacity {ConfiguredMessageCapacity:N0}/16 lane)");
        var world = LoadWorld.Create(1);
        await using var host = await StartAsync(context, world, "bp1");
        using var collector = new MetricCollector(Meters);
        var heapBefore = SettledHeap();
        var clan = world.Clans[0];
        double maxDepth = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                maxDepth = Math.Max(maxDepth, collector.Snapshot().Sum("monze.ingress.message.depth"));
                await Task.Delay(20);
            }
        });
        var baseline = collector.Snapshot();
        var watch = Stopwatch.StartNew();
        for (var wave = 0; wave < total; wave += 1_000)
        {
            await Task.WhenAll(Enumerable.Range(wave, Math.Min(1_000, total - wave))
                .Select(i => host.Inbound.SayAsync(clan.Id, clan.Chat[i % clan.Chat.Count], clan.Members[i % clan.Members.Count], "tràn lane " + i.ToString(CultureInfo.InvariantCulture))));
        }

        watch.Stop();
        await Task.Delay(TimeSpan.FromSeconds(10));
        await sampling.CancelAsync();
        await sampler;
        var delta = collector.Snapshot().Since(baseline);
        var dropped = delta.Sum("monze.ingress.message.dropped");
        var gapChannels = await host.ScalarAsync<long>("SELECT count(*) FROM channel_policy WHERE clan_id = @clan AND has_gap;", ("clan", clan.Id));
        var serving = await LoadProbe.AnswersAsync(host, world, TimeSpan.FromSeconds(10));
        var growth = (SettledHeap() - heapBefore) / (1024.0 * 1024.0);
        return artifact.Metric("pushed", total)
            .Metric("pushPerSecond", total / watch.Elapsed.TotalSeconds)
            .Metric("dropped", dropped)
            .Metric("maxDepth", maxDepth)
            .Metric("gapChannels", gapChannels)
            .Metric("heapGrowthMiB", growth)
            .Invariant("bounded-queue", $"độ sâu ingress ≤ {ConfiguredMessageCapacity:N0}", $"đỉnh {maxDepth:0}", maxDepth <= ConfiguredMessageCapacity)
            .Invariant("drops-marked-as-gap", "tin bị rơi được đánh dấu gap cho channel", $"{dropped:0} rơi, {gapChannels} channel có gap", dropped == 0 || gapChannels > 0)
            .Invariant("still-serving", "sau burst vẫn trả lời lệnh trong 10 s", serving ? "có" : "không", serving)
            .Invariant("bounded-memory", "heap sau GC tăng ≤ 128 MiB", Mib(growth), growth <= 128)
            .Invariant("host-clean", "không có log Critical", $"{host.Logs.CountAtLeast(LogLevel.Critical)}", host.Logs.CountAtLeast(LogLevel.Critical) == 0);
    }

    private static async Task<CampaignArtifact> WelcomeStormAsync(ComponentContext context)
    {
        var joins = context.Full ? 2_000 : 500;
        var artifact = new CampaignArtifact("capacity", "BP-4", $"Bão {joins:N0} join qua hàng đợi welcome");
        var world = LoadWorld.Create(10);
        await using var host = await StartAsync(context, world, "bp4");
        using var collector = new MetricCollector(Meters);
        var tracker = new LoadTracker(world);
        host.Recorder.Recorded += tracker.OnRecorded;
        var welcomeChannels = world.Clans.Select(static clan => clan.Welcome).ToHashSet();
        long welcomes = 0;
        host.Recorder.Recorded += action =>
        {
            if (action.Kind == SimActionKind.SendMessage && welcomeChannels.Contains(action.ChannelId))
            {
                Interlocked.Increment(ref welcomes);
            }
        };
        double maxPending = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                maxPending = Math.Max(maxPending, collector.Snapshot().Sum("monze.ingress.welcome.pending_writers"));
                await Task.Delay(20);
            }
        });
        var baseline = collector.Snapshot();
        var watch = Stopwatch.StartNew();
        for (var wave = 0; wave < joins; wave += 200)
        {
            await Task.WhenAll(Enumerable.Range(wave, Math.Min(200, joins - wave)).Select(i =>
            {
                var user = world.World.NextId();
                tracker.JoinSent(user, Stopwatch.GetTimestamp(), measured: true);
                return host.Inbound.UserAddedAsync(world.Clans[i % world.Clans.Count].Id, user);
            }));
        }

        while (tracker.Unanswered().Joins > 0 && watch.Elapsed < TimeSpan.FromSeconds(180))
        {
            await Task.Delay(250);
        }

        var drained = watch.Elapsed;
        await Task.Delay(TimeSpan.FromSeconds(2));
        await sampling.CancelAsync();
        await sampler;
        var delta = collector.Snapshot().Since(baseline);
        var deliveries = await host.ScalarAsync<long>("SELECT count(*) FROM welcome_delivery;");
        var unwelcomed = tracker.Unanswered().Joins;
        var serving = await LoadProbe.AnswersAsync(host, world, TimeSpan.FromSeconds(10));
        return artifact.Metric("joins", joins)
            .Metric("drainSeconds", drained.TotalSeconds)
            .Metric("welcomesSent", Interlocked.Read(ref welcomes))
            .Metric("unwelcomed", unwelcomed)
            .Metric("welcomeDeliveries", deliveries)
            .Metric("maxPendingWriters", maxPending)
            .Metric("backpressure", delta.Sum("monze.ingress.welcome.backpressure"))
            .Latency("welcome", tracker.Welcomes)
            .Invariant("all-welcomed", $"{joins:N0} người mới đều được chào trong 180 s", $"{unwelcomed:N0} chưa được chào sau {drained.TotalSeconds:0} s", unwelcomed == 0)
            .Invariant("exactly-once", "mỗi người đúng một lời chào và một welcome_delivery", $"{Interlocked.Read(ref welcomes):N0} lời chào, {deliveries:N0} delivery", Interlocked.Read(ref welcomes) == joins && deliveries == joins)
            .Invariant("still-serving", "sau bão join vẫn trả lời lệnh trong 10 s", serving ? "có" : "không", serving)
            .Invariant("host-clean", "không có log Critical", $"{host.Logs.CountAtLeast(LogLevel.Critical)}", host.Logs.CountAtLeast(LogLevel.Critical) == 0);
    }

    private static async Task<CampaignArtifact> CommandStormAsync(ComponentContext context, LoadVariant variant)
    {
        var id = variant == LoadVariant.V1 ? "BP-5-V1" : "BP-5";
        var seconds = context.Full ? 5 : 3;
        var artifact = new CampaignArtifact("capacity", id, $"Bão 2.000 lệnh/s trong {seconds} s ({variant.Id}: {variant.Description})");
        var world = LoadWorld.Create(10);
        await using var host = await StartAsync(context, world, $"bp5_{variant.Id}", new MezonSimulatorOptions { KeepClientRateLimits = variant.KeepClientRateLimits });
        using var collector = new MetricCollector(Meters);
        var heapBefore = SettledHeap();
        var commandChannels = world.Clans.SelectMany(static clan => clan.CommandChannels.Select(channel => (clan, channel))).ToArray();
        long outputs = 0;
        host.Recorder.Recorded += action =>
        {
            if (action.Kind is SimActionKind.SendMessage or SimActionKind.SendEphemeral)
            {
                Interlocked.Increment(ref outputs);
            }
        };
        double maxInflight = 0;
        var random = new Random(context.Seed);
        var watch = Stopwatch.StartNew();
        var sent = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var due = (int)(watch.Elapsed.TotalSeconds * 2_000);
            var batch = new List<Task>();
            for (; sent < due; sent++)
            {
                var (clan, channel) = commandChannels[random.Next(commandChannels.Length)];
                batch.Add(host.Inbound.SayAsync(clan.Id, channel, clan.Members[random.Next(clan.Members.Count)], "*monze help"));
            }

            await Task.WhenAll(batch);
            maxInflight = Math.Max(maxInflight, collector.Snapshot().Sum("monze.command.inflight"));
            await Task.Delay(10);
        }

        var stormSeconds = watch.Elapsed.TotalSeconds;
        var settleWatch = Stopwatch.StartNew();
        while (collector.Snapshot().Sum("monze.command.inflight") > 0 && settleWatch.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(250);
        }

        var settled = collector.Snapshot().Sum("monze.command.inflight") == 0;
        var serving = await LoadProbe.AnswersAsync(host, world, TimeSpan.FromSeconds(10));
        var growth = (SettledHeap() - heapBefore) / (1024.0 * 1024.0);
        var inbox = await host.ScalarAsync<long>("SELECT count(*) FROM command_inbox;");
        artifact.Metric("commandsSent", sent)
            .Metric("commandsPerSecond", sent / stormSeconds)
            .Metric("outputs", Interlocked.Read(ref outputs))
            .Metric("commandInboxRows", inbox)
            .Metric("maxInflight", maxInflight)
            .Metric("settleSeconds", settleWatch.Elapsed.TotalSeconds)
            .Metric("heapGrowthMiB", growth)
            .Invariant("settled", "inflight về 0 trong 60 s sau bão", settled ? $"{settleWatch.Elapsed.TotalSeconds:0.0} s" : "chưa về 0", settled)
            .Invariant("still-serving", "sau bão vẫn trả lời lệnh trong 10 s", serving ? "có" : "không", serving)
            .Invariant("bounded-memory", "heap sau GC tăng ≤ 128 MiB", Mib(growth), growth <= 128)
            .Invariant("host-clean", "không có log Critical", $"{host.Logs.CountAtLeast(LogLevel.Critical)}", host.Logs.CountAtLeast(LogLevel.Critical) == 0);
        if (variant == LoadVariant.V1)
        {
            artifact.KnownGap("DEF-03", "settled").KnownGap("DEF-03", "still-serving");
        }

        return artifact;
    }

    private static async Task<CampaignArtifact> OutboxBacklogAsync(ComponentContext context)
    {
        var rows = context.Full ? 100_000 : 20_000;
        var artifact = new CampaignArtifact("capacity", "BP-6", $"Backlog {rows:N0} dòng outbox due khi khởi động");
        var world = LoadWorld.Create(10);
        var tracker = new LoadTracker(world);
        var nonces = Enumerable.Range(1, rows).Select(static i => LoadStage.OutboxNoncePrefix + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        await using var host = await StartAsync(context, world, "bp6", seed: async database =>
        {
            await world.SeedAsync(database);
            await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await using var command = dataSource.CreateCommand("""
                INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body)
                SELECT clan, channel, 'Announcement', 'load-outbox:' || nonce, 'Thông báo tải ' || nonce
                FROM unnest(@clans, @channels, @nonces) AS row(clan, channel, nonce);
                """);
            command.CommandTimeout = 300;
            command.Parameters.AddWithValue("clans", nonces.Select((_, i) => world.Clans[i % world.Clans.Count].Id).ToArray());
            command.Parameters.AddWithValue("channels", nonces.Select((_, i) => world.Clans[i % world.Clans.Count].Chat[i % 3]).ToArray());
            command.Parameters.AddWithValue("nonces", nonces);
            await command.ExecuteNonQueryAsync();
            var inserted = Stopwatch.GetTimestamp();
            foreach (var nonce in nonces)
            {
                tracker.OutboxRowInserted(nonce, inserted, measured: true);
            }
        }, attach: tracker);
        var heapBefore = SettledHeap();
        var watch = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(rows / 200.0 * 2 + 60);
        while (tracker.Unanswered().Outbox > 0 && watch.Elapsed < limit)
        {
            await Task.Delay(500);
        }

        var drain = watch.Elapsed;
        var undelivered = tracker.Unanswered().Outbox;
        var growth = (SettledHeap() - heapBefore) / (1024.0 * 1024.0);
        return artifact.Metric("rows", rows)
            .Metric("drainSeconds", drain.TotalSeconds)
            .Metric("deliveredPerSecond", (rows - undelivered) / drain.TotalSeconds)
            .Metric("undelivered", undelivered)
            .Metric("duplicates", tracker.OutboxDuplicates)
            .Metric("heapGrowthMiB", growth)
            .Latency("outbox", tracker.Outbox)
            .Invariant("exactly-once", "mọi dòng gửi đúng một lần", $"{undelivered:N0} chưa gửi, {tracker.OutboxDuplicates:N0} trùng", undelivered == 0 && tracker.OutboxDuplicates == 0)
            .Invariant("documented-rate", "xả backlog ≥ 200 dòng/s (tài liệu)", string.Create(CultureInfo.InvariantCulture, $"{(rows - undelivered) / drain.TotalSeconds:0} dòng/s"), (rows - undelivered) / drain.TotalSeconds >= 200)
            .Invariant("bounded-memory", "heap sau GC tăng ≤ 128 MiB", Mib(growth), growth <= 128);
    }

    private static async Task<CampaignArtifact> RejoinStormAsync(ComponentContext context)
    {
        var artifact = new CampaignArtifact("capacity", "BP-7", "Rejoin sau mất socket với 1.000 clan đăng ký, 100 active");
        var world = LoadWorld.Create(1_000);
        var startWatch = Stopwatch.StartNew();
        await using var host = await StartAsync(context, world, "bp7");
        startWatch.Stop();
        var mark = host.Recorder.LastSequence;
        var watch = Stopwatch.StartNew();
        await host.Inbound.CloseSocketAsync();
        var joined = new HashSet<long>();
        while (watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            foreach (var action in host.Recorder.Since(mark).Where(static action => action.Kind == SimActionKind.ClanJoin))
            {
                joined.Add(action.ClanId);
            }

            if (joined.Count >= world.Clans.Count)
            {
                break;
            }

            await Task.Delay(100);
        }

        var rejoin = watch.Elapsed;
        var inactive = await host.ScalarAsync<long>("SELECT count(*) FROM clan_registry WHERE inactive_reason IS NOT NULL;");
        var incomplete = await host.ScalarAsync<bool>("SELECT discovery_incomplete FROM bot_flags WHERE id = 1;");
        var serving = await LoadProbe.AnswersAsync(host, world, TimeSpan.FromSeconds(10));
        return artifact.Metric("timeToReadyMs", startWatch.Elapsed.TotalMilliseconds)
            .Metric("rejoinSeconds", rejoin.TotalSeconds)
            .Metric("clansRejoined", joined.Count)
            .Metric("registryInactive", inactive)
            .Invariant("rejoined", "100 clan active được join lại trong 30 s", $"{joined.Count} clan sau {rejoin.TotalSeconds:0.0} s", joined.Count >= world.Clans.Count && rejoin <= TimeSpan.FromSeconds(30))
            .Invariant("registry-kept", "discovery đầy (100) nên không đánh dấu inactive clan vắng mặt", $"{inactive} inactive, discovery_incomplete={incomplete}", inactive == 0 && incomplete)
            .Invariant("still-serving", "sau rejoin vẫn trả lời lệnh trong 10 s", serving ? "có" : "không", serving);
    }

    private static async Task<CampaignArtifact> DiscoveryBoundaryAsync(ComponentContext context)
    {
        var artifact = new CampaignArtifact("capacity", "BP-8", "Biên discovery: 99 clan thấy được + 5 clan chỉ có trong registry");
        var world = LoadWorld.Create(104, active: 99);
        await using var host = await StartAsync(context, world, "bp8");
        var inactive = await host.ScalarAsync<long>("SELECT count(*) FROM clan_registry WHERE inactive_reason IS NOT NULL;");
        var incomplete = await host.ScalarAsync<bool>("SELECT discovery_incomplete FROM bot_flags WHERE id = 1;");
        return artifact.Metric("registryInactive", inactive)
            .Invariant("discovery-complete", "99 < 100 nên discovery_incomplete = false", $"discovery_incomplete={incomplete}", !incomplete)
            .Invariant("absent-marked-inactive", "5 clan không còn thấy được bị đánh dấu inactive", $"{inactive} inactive", inactive == 5);
    }

    private static async Task<CampaignArtifact> DiscoveryCapAsync(ComponentContext context)
    {
        var artifact = new CampaignArtifact("capacity", "BP-8-DEF06", "Cài mới (registry rỗng), bot là thành viên của 150 clan, discovery trả tối đa 100");
        var world = LoadWorld.Create(150, active: 150);

        // A fresh installation: nothing in clan_registry, so only discovery can find clans.
        await using var host = await StartAsync(context, world, "bp8_def06", seed: static _ => Task.CompletedTask);
        var joined = world.Clans.Count(clan => host.Simulator.ConnectedSessions.Any(session => session.HasJoinedClan(clan.Id)));
        var incomplete = await host.ScalarAsync<bool>("SELECT discovery_incomplete FROM bot_flags WHERE id = 1;");
        return artifact.Metric("memberClans", world.Clans.Count)
            .Metric("joinedClans", joined)
            .Invariant("flagged-incomplete", "discovery đầy được đánh dấu discovery_incomplete", $"{incomplete}", incomplete)
            .Invariant("all-member-clans-joined", "bot join đủ 150 clan mình là thành viên", $"{joined}/{world.Clans.Count}", joined == world.Clans.Count)
            .KnownGap("DEF-06", "all-member-clans-joined");
    }

    private static async Task<SimulatedMonzeHost> StartAsync(
        ComponentContext context,
        LoadWorld world,
        string tag,
        MezonSimulatorOptions? simulator = null,
        Func<Monze.Testing.Postgres.CampaignDatabase, Task>? seed = null,
        LoadTracker? attach = null)
    {
        var host = await SimulatedMonzeHost.StartAsync(world.World, context.Server, $"capacity_{tag}", new SimulatedMonzeHostOptions
        {
            Simulator = simulator,
            Logs = new HostLogSink(LogLevel.Warning, capacity: 20_000),
            SeedAsync = seed ?? world.SeedAsync,
            StartTimeout = TimeSpan.FromMinutes(5),
            Prepare = attach is null ? null : simulator => simulator.Recorder.Recorded += attach.OnRecorded,
            Timings = static timings => timings with
            {
                SchedulerInterval = TimeSpan.FromSeconds(1),
                MaintenanceInterval = TimeSpan.FromMinutes(1),
                OutboxPollInterval = TimeSpan.FromMilliseconds(250),
                RoleScanInterval = TimeSpan.FromSeconds(300),
                UncertainMarkTimeout = TimeSpan.FromSeconds(5)
            }
        });
        host.Recorder.RetainLimit = 20_000;
        return host;
    }

    private static long SettledHeap()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetGCMemoryInfo().HeapSizeBytes;
    }

    private static string Mib(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:0.0} MiB");
}
