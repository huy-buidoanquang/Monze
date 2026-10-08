using Monze.Simulator;
using Monze.Testing.Harness;
using Mezon.Net.Core;

namespace Monze.Campaign.Chaos;

/// <summary>
/// The chaos matrix. Infrastructure faults go through the fault proxies or
/// docker; Mezon and event faults through the simulator's fault plan; clock
/// faults through the host's TimeProvider. Pre-registered defects: DEF-07 (a send acknowledged just
/// before the database blackholes is sent again after the lease expires),
/// DEF-10 (the command
/// rate limiter follows the wall clock backwards) and CAND-21 (outbox sends
/// cut off by a socket close are held as uncertain and never delivered,
/// even when they never reached the wire).
/// </summary>
public static class ChaosScenarios
{
    private const string AgentBlocked = "Cần SimHttpHost cho Agent SSE, transcript và AI (chưa có trong revision này).";
    private const string TwoInstanceBlocked = "Hai instance trên cùng database thuộc workflow E2E (commit 18).";

    public static IReadOnlyList<ChaosScenario> All { get; } =
    [
        // PostgreSQL
        new("PG-01", "PostgreSQL", "treo mạng 20 s (blackhole)", static context => Mode(context.Environment.PostgresProxy, TcpFaultMode.Blackhole), TimeSpan.FromSeconds(20)) { Quick = true },
        new("PG-02", "PostgreSQL", "stop rồi start container", static context => context.Environment.Docker.StopAsync(context.Environment.PostgresContainer, TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(15)),
        new("PG-03", "PostgreSQL", "SIGKILL rồi khởi động lại", static context => context.Environment.Docker.KillAsync(context.Environment.PostgresContainer), TimeSpan.FromSeconds(10)),
        new("PG-04a", "PostgreSQL", "trễ 200 ms mỗi chiều trong 30 s", static context => Latency(context.Environment.PostgresProxy, 200), TimeSpan.FromSeconds(30)),
        new("PG-04b", "PostgreSQL", "trễ 2 s mỗi chiều trong 20 s", static context => Latency(context.Environment.PostgresProxy, 2_000), TimeSpan.FromSeconds(20)),
        new("PG-05", "PostgreSQL", "reset mọi kết nối 3 lần cách 5 s", static context => ResetRepeatedly(context.Environment.PostgresProxy, 3, TimeSpan.FromSeconds(5)), TimeSpan.FromSeconds(15)) { Quick = true },
        new("PG-06", "PostgreSQL", "blackhole ngay sau ack của một lần gửi outbox, 20 s", BlackholeAfterOutboxAck, TimeSpan.FromSeconds(20))
        {
            Observe = TimeSpan.FromSeconds(80),
            KnownDefect = "DEF-07",
            KnownGapInvariants = ["outbox-exactly-once"]
        },
        new("PG-07", "PostgreSQL", "database không sẵn sàng lúc khởi động 20 s", static _ => Task.CompletedTask, TimeSpan.Zero)
        {
            BeforeStart = static context =>
            {
                context.Environment.PostgresProxy.Mode = TcpFaultMode.Refuse;
                _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => context.Environment.PostgresProxy.Mode = TcpFaultMode.Pass, TaskScheduler.Default);
                return Task.CompletedTask;
            }
        },
        new("PG-08", "PostgreSQL", "mất event Agent ended", static _ => Task.CompletedTask, TimeSpan.Zero) { Blocked = AgentBlocked },

        // Redis (optional cache: commands must keep being answered)
        new("RD-01", "Redis", "stop container 20 s", static context => context.Environment.Docker.StopAsync(context.Environment.RedisContainer, TimeSpan.FromSeconds(5)), TimeSpan.FromSeconds(20)) { UsesRedis = true, ServedDuringFault = true },
        // DEF-01 (a slow Redis escapes the fallback) is reproduced at component level
        // (RedisCacheResilienceTests); end to end only welcome settings go through Redis.
        new("RD-02", "Redis", "pause container 10 s (Redis chậm)", static context => context.Environment.Docker.PauseAsync(context.Environment.RedisContainer), TimeSpan.FromSeconds(10))
        {
            UsesRedis = true,
            ServedDuringFault = true,
            Quick = true
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

        // Agent, AI and transcript need HTTP fakes
        new("AG-01", "Agent", "rớt stream SSE", static _ => Task.CompletedTask, TimeSpan.Zero) { Blocked = AgentBlocked },
        new("AG-02", "Agent", "stream SSE treo", static _ => Task.CompletedTask, TimeSpan.Zero) { Blocked = AgentBlocked },
        new("AI-01", "AI/Transcript", "provider AI chậm 35 s", static _ => Task.CompletedTask, TimeSpan.Zero) { Blocked = AgentBlocked },
        new("TR-03", "AI/Transcript", "transcript trả 500", static _ => Task.CompletedTask, TimeSpan.Zero) { Blocked = AgentBlocked },

        // Clock
        new("CLK-01", "Clock", "đồng hồ app nhảy +2 h", static context => Jump(context, TimeSpan.FromHours(2)), TimeSpan.FromSeconds(10)) { Heal = static _ => Task.CompletedTask },
        new("CLK-02", "Clock", "đồng hồ app lùi −2 h", static context => Jump(context, TimeSpan.FromHours(-2)), TimeSpan.FromSeconds(10))
        {
            Heal = static _ => Task.CompletedTask,
            KnownDefect = "DEF-10",
            KnownGapInvariants = ["recovered", "served-after-recovery", "still-serving"]
        },
        new("CLK-04", "Clock", "app lệch database 90 s", static context => Jump(context, TimeSpan.FromSeconds(90)), TimeSpan.FromSeconds(10)) { Heal = static _ => Task.CompletedTask },

        // Process and events
        new("PR-07", "Process", "hai instance chồng nhau 30 s", static _ => Task.CompletedTask, TimeSpan.Zero) { Blocked = TwoInstanceBlocked },
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
}
