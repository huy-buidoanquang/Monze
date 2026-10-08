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
/// - BP-2: the message-gap queue when 300 persisting channels drop messages
///   at once (message capacity 1,024);
/// - BP-3: the Agent ingress queue (capacity 128) and its pending writers
///   under a burst of 2,000 events through the Agent SSE fake.
/// </summary>
public static class CapacityScenarios
{
    private const int ConfiguredMessageCapacity = 8192;
    private static readonly string[] Meters = ["Monze", "System.Runtime"];

    public static IReadOnlyList<(string Id, Func<ComponentContext, Task<CampaignArtifact>> Run)> All { get; } =
    [
        ("BP-1", LaneOverflowAsync),
        ("BP-2", GapQueueOverflowAsync),
        ("BP-3", AgentBurstAsync),
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

    /// <summary>
    /// 300 persisting chat channels (100 clans × 3) receive a burst much
    /// larger than the message ingress holds (the smallest accepted
    /// Monze:Queues:MessageCapacity, 1,024 = 64 per lane; with the default
    /// 8,192 the 16 lanes keep up with one socket), so thousands of messages
    /// drop and each drop queues a gap marker for its channel (capacity
    /// 1,024 / 4 = 256, one consumer that writes one row per channel and
    /// batch). Afterwards the
    /// host is stopped (the SQLite store flushes) and every channel whose
    /// store holds fewer messages than were pushed to it must be marked
    /// has_gap, otherwise an AI summary of that channel would silently miss
    /// messages.
    /// </summary>
    private static async Task<CampaignArtifact> GapQueueOverflowAsync(ComponentContext context)
    {
        const int Wave = 20_000, Capacity = 1_024, GapCapacity = Capacity / 4;
        var total = context.Full ? 150_000 : 60_000;
        var artifact = new CampaignArtifact("capacity", "BP-2", $"Tràn hàng đợi gap: {total:N0} tin vào 300 channel lưu lịch sử cùng lúc (MessageCapacity {Capacity:N0})");
        var world = LoadWorld.Create(100);
        await using var host = await StartAsync(context, world, "bp2", configuration: new Dictionary<string, string?>
        {
            ["Monze:Queues:MessageCapacity"] = Capacity.ToString(CultureInfo.InvariantCulture)
        });
        using var collector = new MetricCollector(Meters);
        var heapBefore = SettledHeap();
        var channels = world.Clans.SelectMany(static clan => clan.Chat.Select(channel => (clan, channel))).ToArray();
        var pushed = new Dictionary<long, long>();
        double maxGapDepth = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                maxGapDepth = Math.Max(maxGapDepth, collector.Snapshot().Sum("monze.ingress.message_gap.depth"));
                await Task.Delay(20);
            }
        });
        var baseline = collector.Snapshot();
        var watch = Stopwatch.StartNew();
        for (var wave = 0; wave < total; wave += Wave)
        {
            var size = Math.Min(Wave, total - wave);
            await Task.WhenAll(Enumerable.Range(wave, size).Select(i =>
            {
                var (clan, channel) = channels[i % channels.Length];
                return host.Inbound.SayAsync(clan.Id, channel, clan.Members[i % clan.Members.Count], "gap " + i.ToString(CultureInfo.InvariantCulture));
            }));
            for (var i = wave; i < wave + size; i++)
            {
                var channel = channels[i % channels.Length].channel;
                pushed[channel] = pushed.GetValueOrDefault(channel) + 1;
            }
        }

        var burstSeconds = watch.Elapsed.TotalSeconds;
        await Task.Delay(TimeSpan.FromSeconds(15));
        await sampling.CancelAsync();
        await sampler;
        var delta = collector.Snapshot().Since(baseline);
        var marked = new HashSet<long>();
        foreach (var row in await host.RowsAsync("SELECT channel_id FROM channel_policy WHERE has_gap;"))
        {
            marked.Add((long)row[0]!);
        }

        var serving = await LoadProbe.AnswersAsync(host, world, TimeSpan.FromSeconds(10));
        var growth = (SettledHeap() - heapBefore) / (1024.0 * 1024.0);
        await host.StopHostAsync();
        var stored = await StoredMessagesAsync(host.DataDirectory, channels.Select(static entry => entry.channel).ToArray());
        var lossy = pushed.Where(entry => stored.GetValueOrDefault(entry.Key) < entry.Value).Select(static entry => entry.Key).ToList();
        var unmarked = lossy.Count(channel => !marked.Contains(channel));

        // A gap marker that finds the gap queue full is dropped with no fallback: its channel
        // keeps has_gap = false although messages are missing.
        return artifact.KnownGap("WF-07", "lossy-channels-marked")
            .Metric("pushed", total)
            .Metric("pushPerSecond", total / burstSeconds)
            .Metric("messagesDropped", delta.Sum("monze.ingress.message.dropped"))
            .Metric("gapMarkersDropped", delta.Sum("monze.ingress.message_gap.dropped"))
            .Metric("maxGapDepth", maxGapDepth)
            .Metric("channelsWithLoss", lossy.Count)
            .Metric("channelsMarked", marked.Count)
            .Metric("lossyUnmarked", unmarked)
            .Metric("messagesStored", stored.Values.Sum())
            .Metric("heapGrowthMiB", growth)
            .Invariant("bounded-gap-queue", $"hàng đợi gap ≤ {GapCapacity:N0} (MessageCapacity / 4)", $"đỉnh {maxGapDepth:0}", maxGapDepth <= GapCapacity)
            .Invariant("lossy-channels-marked", "mọi channel mất tin (SQLite ít hơn số tin đã đẩy) được đánh dấu has_gap", $"{unmarked} chưa đánh dấu / {lossy.Count} channel mất tin", unmarked == 0)
            .Invariant("still-serving", "sau burst vẫn trả lời lệnh trong 10 s", serving ? "có" : "không", serving)
            .Invariant("bounded-memory", "heap sau GC tăng ≤ 128 MiB", Mib(growth), growth <= 128)
            .Invariant("host-clean", "không có log Critical", $"{host.Logs.CountAtLeast(LogLevel.Critical)}", host.Logs.CountAtLeast(LogLevel.Critical) == 0);
    }

    /// <summary>Messages per channel in the SDK's SQLite message store of a stopped host.</summary>
    private static async Task<Dictionary<long, long>> StoredMessagesAsync(DirectoryInfo dataDirectory, long[] channels)
    {
        var counts = new Dictionary<long, long>();
        foreach (var file in new DirectoryInfo(Path.Combine(dataDirectory.FullName, "data")).GetFiles("*.db"))
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file.FullName};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT channel_id, count(*) FROM messages GROUP BY channel_id;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var channel = reader.GetInt64(0);
                if (channels.Contains(channel))
                {
                    counts[channel] = counts.GetValueOrDefault(channel) + reader.GetInt64(1);
                }
            }
        }

        return counts;
    }

    /// <summary>
    /// 1,998 Agent events (666 rooms × started, ended, summary done) burst
    /// through the Agent SSE fake into an Agent ingress of capacity 128 (the
    /// smallest Monze:Queues:AgentCapacity accepts). The SDK does not wait
    /// for Monze's handler, so every event past the capacity becomes a
    /// pending writer. Every room must still end up with its summary posted,
    /// the queue must stay bounded and the pending writers must drain.
    /// </summary>
    private static async Task<CampaignArtifact> AgentBurstAsync(ComponentContext context)
    {
        const int Rooms = 666, Events = Rooms * 3;
        var artifact = new CampaignArtifact("capacity", "BP-3", $"Burst {Events:N0} event Agent vào hàng đợi Agent capacity 128");
        var world = LoadWorld.Create(10);
        var clans = Chaos.AgentClan.AddTo(world.World);
        await using var http = await SimHttpHost.StartAsync(new SimHttpHostOptions { BotId = world.BotId, BotToken = world.World.Bot.Token });
        await using var host = await StartAsync(context, world, "bp3", configuration: new Dictionary<string, string?>
        {
            ["Mezon:AgentBaseUrl"] = http.BaseUrl,
            ["Monze:Queues:AgentCapacity"] = "128"
        });
        await http.WaitForSseAsync(1, TimeSpan.FromSeconds(30));
        using var collector = new MetricCollector(Meters);
        var heapBefore = SettledHeap();
        var rooms = new List<(string Room, long Clan, long Voice)>();
        for (var i = 0; i < Rooms; i++)
        {
            var clan = clans[i % clans.Count];
            var room = $"bp3-room-{i.ToString(CultureInfo.InvariantCulture)}";
            rooms.Add((room, clan.Id, clan.Voices[(i / clans.Count) % clan.Voices.Count]));
            http.SetSummary(room, SimHttpHost.SummaryJson(room, "Tóm tắt burst."));
        }

        double maxDepth = 0, maxPending = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                var snapshot = collector.Snapshot();
                maxDepth = Math.Max(maxDepth, snapshot.Sum("monze.ingress.agent.depth"));
                maxPending = Math.Max(maxPending, snapshot.Sum("monze.ingress.agent.pending_writers"));
                await Task.Delay(20);
            }
        });
        var baseline = collector.Snapshot();
        var watch = Stopwatch.StartNew();
        var lost = 0;
        foreach (var (room, clan, voice) in rooms)
        {
            foreach (var type in new[] { "room_started", "room_ended", "room_summary_done" })
            {
                lost += await http.PublishAgentEventAsync(type, room, voice, clan) == 0 ? 1 : 0;
            }
        }

        var publishSeconds = watch.Elapsed.TotalSeconds;
        long posted = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(180))
        {
            posted = await host.ScalarAsync<long>("SELECT count(*) FROM meeting_session WHERE room_id LIKE 'bp3-room-%' AND status = 'posted';");
            if (posted == rooms.Count)
            {
                break;
            }

            await Task.Delay(500);
        }

        var drained = watch.Elapsed;
        await Task.Delay(TimeSpan.FromSeconds(2));
        await sampling.CancelAsync();
        await sampler;
        var settled = collector.Snapshot();
        var delta = settled.Since(baseline);
        var serving = await LoadProbe.AnswersAsync(host, world, TimeSpan.FromSeconds(10));
        var growth = (SettledHeap() - heapBefore) / (1024.0 * 1024.0);
        var pendingAfter = settled.Sum("monze.ingress.agent.pending_writers");
        return artifact.Metric("events", Events)
            .Metric("eventsLostUpstream", lost)
            .Metric("publishSeconds", publishSeconds)
            .Metric("drainSeconds", drained.TotalSeconds)
            .Metric("roomsPosted", posted)
            .Metric("maxDepth", maxDepth)
            .Metric("maxPendingWriters", maxPending)
            .Metric("backpressure", delta.Sum("monze.ingress.agent.backpressure"))
            .Metric("eventsIgnored", delta.Sum("monze.agent.events.ignored"))
            .Metric("heapGrowthMiB", growth)
            .Invariant("all-posted", $"{rooms.Count} phòng đều được tóm tắt trong 180 s", $"{posted}/{rooms.Count} sau {drained.TotalSeconds:0} s", posted == rooms.Count)
            .Invariant("bounded-queue", "hàng đợi Agent ≤ 128", $"đỉnh {maxDepth:0}", maxDepth <= 128)
            .Invariant("pending-drained", "pending writer về 0 sau burst", $"{pendingAfter:0}", pendingAfter == 0)
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
        LoadTracker? attach = null,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var host = await SimulatedMonzeHost.StartAsync(world.World, context.Server, $"capacity_{tag}", new SimulatedMonzeHostOptions
        {
            Simulator = simulator,
            Configuration = configuration ?? new Dictionary<string, string?>(),
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
