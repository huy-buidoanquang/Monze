using Monze.Simulator;
using Monze.Testing.Harness;
using System.Globalization;
using Mezon.Net.Core;
using Npgsql;

namespace Monze.Campaign.Chaos;

/// <summary>
/// The chaos matrix. Infrastructure faults go through the fault proxies or
/// docker; Mezon and event faults through the simulator's fault plan; clock
/// faults through the host's TimeProvider. Pre-registered defects: DEF-07 (a send acknowledged just
/// before the database blackholes is sent again after the lease expires),
/// DEF-10 (the command
/// rate limiter follows the wall clock backwards) and CAND-21 (outbox sends
/// cut off by a socket close are held as uncertain and never delivered,
/// even when they never reached the wire). Agent, AI and transcript faults go
/// through the HTTP fake (SimHttpHost) with Agent meeting cycles and AI
/// commands as extra background traffic; process faults start, stop and
/// kill further host instances on the same database and platform.
/// </summary>
public static class ChaosScenarios
{
    private const int Many = 100_000;

    public static IReadOnlyList<ChaosScenario> All { get; } =
    [
        // PostgreSQL
        new("PG-01", "PostgreSQL", "treo mạng 20 s (blackhole)", static context => Mode(context.Environment.PostgresProxy, TcpFaultMode.Blackhole), TimeSpan.FromSeconds(20)) { Quick = true },
        new("PG-02", "PostgreSQL", "stop rồi start container", static context => context.Environment.Docker.StopAsync(context.Environment.PostgresContainer, TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(15)),
        new("PG-03", "PostgreSQL", "SIGKILL rồi khởi động lại", static context => context.Environment.Docker.KillAsync(context.Environment.PostgresContainer), TimeSpan.FromSeconds(10)),
        new("PG-04a", "PostgreSQL", "trễ 200 ms mỗi chiều trong 30 s", static context => Latency(context.Environment.PostgresProxy, 200), TimeSpan.FromSeconds(30)),
        new("PG-04b", "PostgreSQL", "trễ 2 s mỗi chiều trong 20 s", static context => Latency(context.Environment.PostgresProxy, 2_000), TimeSpan.FromSeconds(20))
        {
            // A 256-row batch outlives its 60 s lease at this latency and is reclaimed and resent.
            KnownDefect = "DEF-07",
            KnownGapInvariants = ["outbox-exactly-once"]
        },
        new("PG-05", "PostgreSQL", "reset mọi kết nối 3 lần cách 5 s", static context => ResetRepeatedly(context.Environment.PostgresProxy, 3, TimeSpan.FromSeconds(5)), TimeSpan.FromSeconds(15)) { Quick = true },
        new("PG-06", "PostgreSQL", "blackhole ngay sau ack của một lần gửi outbox, 20 s", BlackholeAfterOutboxAck, TimeSpan.FromSeconds(20))
        {
            Observe = TimeSpan.FromSeconds(80),
            KnownDefect = "DEF-07",
            KnownGapInvariants = ["outbox-exactly-once"]
        },
        new("PG-07", "PostgreSQL", "database không sẵn sàng lúc khởi động 20 s", static _ => Task.CompletedTask, TimeSpan.Zero)
        {
            // Startup validates the schema once and stops the host on the first connection error.
            KnownDefect = "CAND-29",
            KnownGapInvariants = ["completed"],
            BeforeStart = static context =>
            {
                context.Environment.PostgresProxy.Mode = TcpFaultMode.Refuse;
                _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => context.Environment.PostgresProxy.Mode = TcpFaultMode.Pass, TaskScheduler.Default);
                return Task.CompletedTask;
            }
        },
        new("PG-08", "PostgreSQL", "mất event Agent ended trong 20 s (upstream không giao room_ended)", static context => Agent(context, static agent => agent.LoseEnded = true), TimeSpan.FromSeconds(20))
        {
            // DEF-12 (a session live forever) needs room_summary_done to be lost too: here it
            // still arrives and posts the summary, and the empty voice room releases the claim.
            UsesHttp = true
        },

        // Redis (optional cache: commands must keep being answered)
        new("RD-01", "Redis", "stop container 20 s", static context => context.Environment.Docker.StopAsync(context.Environment.RedisContainer, TimeSpan.FromSeconds(5)), TimeSpan.FromSeconds(20)) { UsesRedis = true, ServedDuringFault = true },
        new("RD-02", "Redis", "pause container 10 s (Redis chậm)", static context => context.Environment.Docker.PauseAsync(context.Environment.RedisContainer), TimeSpan.FromSeconds(10))
        {
            UsesRedis = true,
            ServedDuringFault = true,
            Quick = true,

            // A paused Redis times out (RedisTimeoutException is not a RedisException): no fallback
            // cooldown, so every welcome read during the pause waits for the Redis timeout first.
            KnownDefect = "DEF-01",
            KnownGapInvariants = ["welcome-fast-during-fault"]
        },
        new("RD-03", "Redis", "trễ 200 ms mỗi chiều trong 20 s", static context => Latency(context.Environment.RedisProxy, 200), TimeSpan.FromSeconds(20)) { UsesRedis = true, ServedDuringFault = true },
        new("RD-04", "Redis", "reset kết nối (mất pub/sub) 3 lần", static context => ResetRepeatedly(context.Environment.RedisProxy, 3, TimeSpan.FromSeconds(5)), TimeSpan.FromSeconds(15)) { UsesRedis = true, ServedDuringFault = true },
        new("RD-05", "Redis", "mất toàn bộ dữ liệu (FLUSHALL)", static context => context.Environment.FlushRedisAsync(), TimeSpan.FromSeconds(5)) { UsesRedis = true, ServedDuringFault = true },
        new("RD-06", "Redis", "Redis xuất hiện sau khi bot đã chạy", static _ => Task.CompletedTask, TimeSpan.FromSeconds(5))
        {
            UsesRedis = true,
            BeforeStart = static context => context.Environment.Docker.StopAsync(context.Environment.RedisContainer, TimeSpan.FromSeconds(5))
        },

        // Mezon (simulator)
        new("MZ-01", "Mezon", "server đóng socket", static context => Close(context, 1, TimeSpan.Zero), TimeSpan.FromSeconds(5))
        {
            Quick = true,
            KnownDefect = "CAND-21",
            KnownGapInvariants = ["outbox-exactly-once"]
        },
        new("MZ-02", "Mezon", "20 lần đóng socket trong 60 s", static context => Close(context, 20, TimeSpan.FromSeconds(3)), TimeSpan.FromSeconds(60))
        {
            KnownDefect = "CAND-21",
            KnownGapInvariants = ["outbox-exactly-once"]
        },
        new("MZ-03", "Mezon", "ack gửi tin chậm 2 s trong 30 s", static context => Faults(context, plan => plan.Delay(SimOperations.ChannelMessageSend, TimeSpan.FromSeconds(2), times: 10_000).Delay(SimOperations.EphemeralMessageSend, TimeSpan.FromSeconds(2), times: 10_000)), TimeSpan.FromSeconds(30)),
        // Pre-registered DEF-09 (unbounded wait) did not reproduce: the SDK times the ack out,
        // the row is held as uncertain and the message (already on the wire) is not resent.
        new("MZ-04", "Mezon", "mất ack của 50 lần gửi", static context => Faults(context, plan => plan.DropResponse(SimOperations.ChannelMessageSend, times: 50)), TimeSpan.FromSeconds(20)) { Quick = true },
        new("MZ-05", "Mezon", "ack lỗi 403/404/500 cho 60 lần gửi", static context => Faults(context, plan => plan
            .Fail(SimOperations.ChannelMessageSend, MezonStatusCode.PermissionDenied, times: 20)
            .Fail(SimOperations.ChannelMessageSend, MezonStatusCode.NotFound, times: 20)
            .Fail(SimOperations.ChannelMessageSend, MezonStatusCode.Internal, times: 20)), TimeSpan.FromSeconds(20)),
        new("MZ-07", "Mezon", "đăng nhập/handshake bị từ chối 5 lần sau khi mất socket", static async context =>
        {
            await Faults(context, plan => plan.RefuseConnect(times: 5));
            await context.RequireHost().Inbound.CloseSocketAsync();
        }, TimeSpan.FromSeconds(20)) { KnownDefect = "CAND-21", KnownGapInvariants = ["outbox-exactly-once"] },
        new("MZ-08", "Mezon", "discovery lỗi 3 lần khi kết nối lại", static async context =>
        {
            await Faults(context, plan => plan.Fail(SimOperations.ListClanDescs, MezonStatusCode.Unavailable, times: 3));
            await context.RequireHost().Inbound.CloseSocketAsync();
        }, TimeSpan.FromSeconds(20)) { KnownDefect = "CAND-21", KnownGapInvariants = ["outbox-exactly-once"] },

        // Agent SSE (SimHttpHost), with Agent meeting cycles and AI commands as extra load
        new("AG-01", "Agent", "server ngắt stream SSE 3 lần cách 7 s", static context => Repeat(TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(7), () =>
        {
            context.RequireHttp().DropSseStreams();
            return Task.CompletedTask;
        }), TimeSpan.FromSeconds(21))
        {
            UsesHttp = true,
            Quick = true,

            // Events published in the 3–4 s before the SDK reconnects are never replayed (no
            // Last-Event-ID): meetings whose started or ended event fell in a gap are never summarized.
            KnownDefect = "DEF-08",
            KnownGapInvariants = ["meetings-summarized"]
        },
        new("AG-02", "Agent", "stream SSE half-open (kết nối còn, không byte nào tới) và không bao giờ được thay", static context =>
        {
            context.RequireHttp().HangSseStreams();
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(20))
        {
            UsesHttp = true,

            // No idle detection: the dead stream is never replaced, so nothing after the fault is processed.
            KnownDefect = "DEF-08",
            KnownGapInvariants = ["agent-recovered", "meetings-summarized"]
        },
        new("AG-03", "Agent", "mọi event Agent được giao hai lần trong 20 s", static context => Agent(context, static agent => agent.Delivery = SimSseDelivery.Duplicate), TimeSpan.FromSeconds(20)) { UsesHttp = true },
        new("AG-04", "Agent", "room_ended tới trước room_started trong 20 s", static context => Agent(context, static agent => agent.Reorder = true), TimeSpan.FromSeconds(20)) { UsesHttp = true },
        new("AG-05", "Agent", "event hỏng, JSON không phải object và event của clan khác trong 20 s", static context => Repeat(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(500), () => JunkAsync(context)), TimeSpan.FromSeconds(20))
        {
            UsesHttp = true,
            Verify = VerifyJunkAsync
        },
        new("AG-06", "Agent", "burst 1.998 event Agent (666 phòng × 3 event)", static context => context.RequireAgent().BurstAsync(2_000), TimeSpan.FromSeconds(20))
        {
            UsesHttp = true,
            Observe = TimeSpan.FromSeconds(60)
        },

        // AI provider and transcript (SimHttpHost)
        new("AI-01", "AI/Transcript", "provider AI chậm 35 s (client timeout 30 s)", static context => Http(context, static plan => plan.Delay(SimHttpRoute.AiCompletion, TimeSpan.FromSeconds(35), Many)), TimeSpan.FromSeconds(40))
        {
            UsesHttp = true,
            ServedDuringFault = true
        },
        new("AI-02", "AI/Transcript", "provider AI trả 429/500/503 xen kẽ", static context => Http(context, static plan =>
        {
            for (var i = 0; i < 3_000; i++)
            {
                plan.Status(SimHttpRoute.AiCompletion, (i % 3) switch { 0 => 429, 1 => 500, _ => 503 });
            }
        }), TimeSpan.FromSeconds(20))
        {
            UsesHttp = true,
            ServedDuringFault = true,
            Quick = true
        },
        new("AI-03", "AI/Transcript", "provider AI trả body 3 MiB (giới hạn 2 MiB)", static context => Http(context, static plan => plan.Oversized(SimHttpRoute.AiCompletion, 3 * 1024 * 1024, Many)), TimeSpan.FromSeconds(20))
        {
            UsesHttp = true,
            ServedDuringFault = true
        },
        new("AI-04", "AI/Transcript", "provider AI nhỏ giọt body 150 ms/byte", static context => Http(context, static plan => plan.Drip(SimHttpRoute.AiCompletion, TimeSpan.FromMilliseconds(150), Many)), TimeSpan.FromSeconds(30))
        {
            UsesHttp = true,
            ServedDuringFault = true
        },
        new("AI-05", "AI/Transcript", "provider AI trả JSON hỏng", static context => Http(context, static plan => plan.Malformed(SimHttpRoute.AiCompletion, Many)), TimeSpan.FromSeconds(20))
        {
            UsesHttp = true,
            ServedDuringFault = true
        },
        new("AI-06", "AI/Transcript", "AI bão hoà: provider chậm 10 s và 10 lệnh AI/s trong 30 s", static context =>
        {
            context.RequireHttp().Faults.Delay(SimHttpRoute.AiCompletion, TimeSpan.FromSeconds(10), Many);
            context.RequireAgent().AiInterval = TimeSpan.FromMilliseconds(100);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(30))
        {
            UsesHttp = true,
            ServedDuringFault = true
        },
        new("TR-01", "AI/Transcript", "transcript treo (client timeout 30 s) trong 40 s", static context => Http(context, static plan => plan.Hang(SimHttpRoute.TranscriptSummary, Many)), TimeSpan.FromSeconds(40))
        {
            UsesHttp = true,
            WarpSummaryRetries = true,

            // The Agent path fetches the transcript inside the single meeting-ingress reader:
            // each hung fetch holds every Agent and voice-empty event for up to 30 s.
            KnownDefect = "WF-04",
            KnownGapInvariants = ["agent-events-timely"]
        },
        new("TR-02", "AI/Transcript", "access token transcript bị thu hồi mỗi 3 s (401 → refresh)", static context => Repeat(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(3), () =>
        {
            context.RequireHttp().RevokeAccessTokens();
            return Task.CompletedTask;
        }), TimeSpan.FromSeconds(20))
        {
            UsesHttp = true
        },
        new("TR-03", "AI/Transcript", "transcript trả 500 trong 30 s", static context => Http(context, static plan => plan.Status(SimHttpRoute.TranscriptSummary, 500, Many)), TimeSpan.FromSeconds(30))
        {
            UsesHttp = true,
            WarpSummaryRetries = true
        },
        new("TR-04", "AI/Transcript", "transcript hợp lệ 600 KiB (giới hạn 512 KiB) trong 20 s", static context => Agent(context, static agent => agent.LargeTranscripts = true), TimeSpan.FromSeconds(20))
        {
            UsesHttp = true,
            WarpSummaryRetries = true,
            Timings = static timings => timings with { MaintenanceInterval = TimeSpan.FromSeconds(2) },

            // HttpPayloadLimits.TranscriptResponseBytes = 512 KiB: a valid larger transcript is
            // treated as "no summary" on every retry and the meeting is never summarized.
            KnownDefect = "WF-03",
            KnownGapInvariants = ["large-summaries-posted"]
        },
        new("TR-05", "AI/Transcript", "transcript rỗng: tám lần retry hết hạn (retry được warp)", static context =>
        {
            context.RequireHttp().Faults.Empty(SimHttpRoute.TranscriptSummary, Many);
            return Repeat(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1), () => context.RequireAgent().WarpRetriesAsync());
        }, TimeSpan.FromSeconds(60))
        {
            UsesHttp = true,
            WarpSummaryRetries = true,
            Timings = static timings => timings with { MaintenanceInterval = TimeSpan.FromSeconds(2) }
        },

        // Clock
        new("CLK-01", "Clock", "đồng hồ app nhảy +2 h", static context => Jump(context, TimeSpan.FromHours(2)), TimeSpan.FromSeconds(10)) { Heal = static _ => Task.CompletedTask },
        new("CLK-02", "Clock", "đồng hồ app lùi −2 h", static context => Jump(context, TimeSpan.FromHours(-2)), TimeSpan.FromSeconds(10))
        {
            Heal = static _ => Task.CompletedTask,
            KnownDefect = "DEF-10",
            KnownGapInvariants = ["recovered", "served-after-recovery", "still-serving"]
        },
        new("CLK-04", "Clock", "app lệch database 90 s", static context => Jump(context, TimeSpan.FromSeconds(90)), TimeSpan.FromSeconds(10)) { Heal = static _ => Task.CompletedTask },
        new("CLK-03", "Clock", "đồng hồ app nhảy tới trước ngày đổi giờ mùa hè (Europe/Berlin 28/03/2027) khi lịch đến hạn", DstJumpAsync, TimeSpan.FromSeconds(15))
        {
            Heal = static context =>
            {
                context.Clock.Offset = TimeSpan.Zero;
                return Task.CompletedTask;
            },
            Verify = VerifyDstAsync,

            // The jump is ~170 days; a 30-day inbox retention would purge every inbox row
            // on the next maintenance pass and hide the DST behaviour behind that effect.
            Timings = static timings => timings with { InboxRetention = TimeSpan.FromDays(400) },

            // The next 02:30 is looked up only today and tomorrow (daily) or on the next Sunday
            // (weekly); it does not exist, so the worker completes the schedule before firing the
            // due occurrence: that occurrence and every later one are lost.
            KnownDefect = "CAND-09",
            KnownGapInvariants = ["dst-recurring-survive"]
        },

        // Process and events
        // "Kill" in-process: the platform drops the host's socket, then the host stops with an
        // already-cancelled token (no drain: leases stay taken, queues and SQLite are not
        // flushed) and is disposed; code already running finishes its current await first.
        new("PR-01", "Process", "kill Monze giữa tải, khởi động lại sau 10 s trên cùng database", static context => context.RequireHost().KillHostAsync(), TimeSpan.FromSeconds(10))
        {
            UsesHttp = true,
            ChecksSingleAnswer = true,
            Observe = TimeSpan.FromSeconds(70),
            Heal = HealWithRestartAsync,

            // Agent events published while no instance is subscribed are never replayed (no Last-Event-ID).
            KnownDefect = "DEF-08",
            KnownGapInvariants = ["meetings-summarized"]
        },
        new("PR-06", "Process", "dừng Monze bình thường giữa tải khi outbox đang gửi (ack chậm 500 ms), khởi động lại sau 15 s trên cùng database", static async context =>
        {
            // Slow acks keep outbox sends in flight when the stop begins (with 30 ms acks only a few are).
            var host = context.RequireHost();
            host.Simulator.Faults.Delay(SimOperations.ChannelMessageSend, TimeSpan.FromMilliseconds(500), times: 1_000);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            await host.StopHostAsync();
        }, TimeSpan.FromSeconds(15))
        {
            UsesHttp = true,
            ChecksSingleAnswer = true,
            Heal = HealWithRestartAsync,
            KnownDefect = "DEF-08",
            KnownGapInvariants = ["meetings-summarized"],

            // Sends in flight at the stop are acked, but their completion runs on the cancelled
            // stopping token: the rows stay leased and are sent again once the lease expires.
            // An AI command in flight is cancelled silently: its loading card is never updated.
            OtherKnownGaps = [("WF-05", "outbox-exactly-once"), ("WF-06", "ai-answered")]
        },
        new("PR-07", "Process", "hai instance chồng nhau 30 s trên cùng database và platform, rồi một instance dừng", static context => context.StartInstanceAsync(sameDataDirectory: false), TimeSpan.FromSeconds(30))
        {
            UsesHttp = true,
            ChecksSingleAnswer = true,

            // Both instances are healthy while they overlap: no command may be lost or slowed.
            ServedDuringFault = true,
            Heal = static async context =>
            {
                await context.Instances[^1].StopHostAsync();
                await DefaultHealAsync(context);
            }
        },
        new("EV-01", "Event", "mọi tin nhắn được giao hai lần trong 20 s", static context => Faults(context, plan => plan.DuplicatePush(SimPushKind.ChannelMessage, times: 10_000)), TimeSpan.FromSeconds(20)) { Quick = true },
        new("EV-03", "Event", "sự kiện voice đảo thứ tự trong 20 s", static context => Faults(context, plan => plan.ReorderPush(SimPushKind.VoiceJoinedEvent, times: 10_000)), TimeSpan.FromSeconds(20)),
        new("EV-04", "Event", "join được giao hai lần trong 20 s", static context => Faults(context, plan => plan.DuplicatePush(SimPushKind.AddClanUserEvent, times: 10_000)), TimeSpan.FromSeconds(20))
    ];

    private static Task Mode(TcpFaultProxy proxy, TcpFaultMode mode)
    {
        proxy.Mode = mode;
        return Task.CompletedTask;
    }

    private static Task Latency(TcpFaultProxy proxy, int milliseconds)
    {
        proxy.Latency = TimeSpan.FromMilliseconds(milliseconds);
        return Task.CompletedTask;
    }

    private static Task ResetRepeatedly(TcpFaultProxy proxy, int times, TimeSpan every)
    {
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < times; i++)
            {
                proxy.ResetConnections();
                await Task.Delay(every);
            }
        });
        return Task.CompletedTask;
    }

    private static Task Faults(ChaosContext context, Action<SimFaultPlan> plan)
    {
        plan(context.RequireHost().Simulator.Faults);
        return Task.CompletedTask;
    }

    private static Task Close(ChaosContext context, int times, TimeSpan every)
    {
        var host = context.RequireHost();
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < times; i++)
            {
                await host.Inbound.CloseSocketAsync();
                await Task.Delay(every);
            }
        });
        return Task.CompletedTask;
    }

    private static Task Jump(ChaosContext context, TimeSpan offset)
    {
        context.Clock.Offset = offset;
        return Task.CompletedTask;
    }

    /// <summary>Blackholes PostgreSQL the moment the simulator records the next outbox send (the ack is already on its way).</summary>
    private static Task BlackholeAfterOutboxAck(ChaosContext context)
    {
        var host = context.RequireHost();
        var proxy = context.Environment.PostgresProxy;
        var armed = 1;
        void OnRecorded(SimAction action)
        {
            if (action.Kind == SimActionKind.SendMessage
                && action.ContentJson?.Contains(Load.LoadStage.OutboxNoncePrefix, StringComparison.Ordinal) == true
                && Interlocked.Exchange(ref armed, 0) == 1)
            {
                proxy.Mode = TcpFaultMode.Blackhole;
                host.Recorder.Recorded -= OnRecorded;
            }
        }

        host.Recorder.Recorded += OnRecorded;
        return Task.CompletedTask;
    }

    private static Task Agent(ChaosContext context, Action<AgentAiTraffic> change)
    {
        change(context.RequireAgent());
        return Task.CompletedTask;
    }

    private static Task Http(ChaosContext context, Action<SimHttpFaultPlan> plan)
    {
        plan(context.RequireHttp().Faults);
        return Task.CompletedTask;
    }

    /// <summary>Runs <paramref name="action"/> every <paramref name="every"/> for <paramref name="duration"/>, in the background.</summary>
    private static Task Repeat(TimeSpan duration, TimeSpan every, Func<Task> action)
    {
        _ = Task.Run(async () =>
        {
            var until = DateTimeOffset.UtcNow + duration;
            while (DateTimeOffset.UtcNow < until)
            {
                try
                {
                    await action();
                }
                catch (Exception)
                {
                    // A failed repetition (e.g. a warp racing a host restart) is retried on the next tick.
                }

                await Task.Delay(every);
            }
        });
        return Task.CompletedTask;
    }

    private static Task DefaultHealAsync(ChaosContext context) => context.HealDefaultsAsync();

    /// <summary>Starts a new host on the stopped (or killed) host's database and data directory, then the default heal.</summary>
    private static async Task HealWithRestartAsync(ChaosContext context)
    {
        await context.RestartAsync();
        await context.HealDefaultsAsync();
    }

    /// <summary>
    /// Junk on the Agent stream: a frame that is not JSON, JSON that is not
    /// an object (CAND-01), a started event for a clan without the bot, one
    /// whose voice room belongs to another clan, and an ended event for a room
    /// nobody started.
    /// </summary>
    private static async Task JunkAsync(ChaosContext context)
    {
        var http = context.RequireHttp();
        var world = context.World!;
        await http.PublishRawAsync("data: {not json\n\nevent: room_started\ndata: [1,2\n\n");
        await http.PublishJsonAsync("room_started", "[1,2]");
        await http.PublishAgentEventAsync("room_started", "chaos-junk-" + Guid.NewGuid().ToString("N"), world.Clans[0].Voice[0], 1_860_000_000_000_999_999L);
        await http.PublishAgentEventAsync("room_started", "chaos-junk-" + Guid.NewGuid().ToString("N"), world.Clans[0].Voice[0], world.Clans[1].Id);
        await http.PublishAgentEventAsync("room_ended", "chaos-junk-" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Three Europe/Berlin schedules due now (daily 02:30, weekly Sunday 02:30
    /// and a daily 09:00 control) in the last load clan, and the app clock on
    /// Saturday 27/03/2027 11:00 CET: the next 02:30 (Sunday 28/03) does not
    /// exist because the clocks go from 02:00 to 03:00.
    /// </summary>
    private static async Task DstJumpAsync(ChaosContext context)
    {
        var host = context.RequireHost();
        var clan = context.World!.Clans[^1];
        context.Clock.Offset = new DateTimeOffset(2027, 3, 27, 10, 0, 0, TimeSpan.Zero) - DateTimeOffset.UtcNow;
        await using var connection = new NpgsqlConnection(host.Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO meeting_schedule(clan_id, channel_id, requester_id, title, kind, when_text, timezone, next_run_at, status)
            SELECT @clan, @channel, @owner, title, kind, when_text, 'Europe/Berlin', now() - interval '1 second', 'active'
            FROM (VALUES ('DST daily 02:30', 'Daily', '02:30'), ('DST weekly 02:30', 'Weekly', 'cn 02:30'), ('DST daily 09:00', 'Daily', '09:00')) AS schedule(title, kind, when_text);
            """, connection);
        command.Parameters.AddWithValue("clan", clan.Id);
        command.Parameters.AddWithValue("channel", clan.Chat[2]);
        command.Parameters.AddWithValue("owner", clan.Owner);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Each DST schedule fired its due occurrence once and is still active on its next occurrence.</summary>
    private static async Task VerifyDstAsync(ChaosContext context, CampaignArtifact artifact)
    {
        await using var connection = new NpgsqlConnection(context.RequireHost().Database.ConnectionString);
        await connection.OpenAsync();
        var schedules = new Dictionary<string, (string Status, DateTime? Next)>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT title, status, next_run_at FROM meeting_schedule WHERE title LIKE 'DST %';", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                schedules[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDateTime(2));
            }
        }

        var fired = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT meeting_title, count(*) FROM meeting_session WHERE meeting_title LIKE 'DST %' GROUP BY meeting_title;", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                fired[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        var boundary = new DateTime(2027, 3, 28, 1, 0, 0, DateTimeKind.Utc);
        string Describe(string title)
            => schedules.TryGetValue(title, out var row)
                ? string.Create(CultureInfo.InvariantCulture, $"{title}: {row.Status}, next {row.Next:yyyy-MM-dd HH:mm}Z, {fired.GetValueOrDefault(title)} lần chạy")
                : $"{title}: không có";
        bool Survived(string title)
            => schedules.TryGetValue(title, out var row) && row.Status == "active" && row.Next > boundary && fired.GetValueOrDefault(title) == 1;
        artifact.Metric("dstSchedulesFired", fired.Values.Sum())
            .Invariant("dst-control", "lịch daily 09:00 chạy một lần và chuyển sang 28/03 09:00 CEST", Describe("DST daily 09:00"), Survived("DST daily 09:00"))
            .Invariant("dst-recurring-survive", "lịch daily/weekly 02:30 chạy lần đến hạn một lần và vẫn active sau ngày đổi giờ", $"{Describe("DST daily 02:30")}; {Describe("DST weekly 02:30")}", Survived("DST daily 02:30") && Survived("DST weekly 02:30"));
    }

    /// <summary>No junk event created a meeting session.</summary>
    private static async Task VerifyJunkAsync(ChaosContext context, CampaignArtifact artifact)
    {
        await using var connection = new NpgsqlConnection(context.RequireHost().Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM meeting_session WHERE room_id LIKE 'chaos-junk-%';", connection);
        var sessions = (long)(await command.ExecuteScalarAsync())!;
        artifact.Invariant("junk-ignored", "event rác, ngoài phạm vi hoặc của phòng không ai mở không tạo phiên nào", $"{sessions} phiên", sessions == 0);
    }
}
