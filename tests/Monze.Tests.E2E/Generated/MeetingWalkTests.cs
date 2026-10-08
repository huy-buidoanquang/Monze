using System.Globalization;
using Mezon.Net.Core;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Monze.Ui;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Generated;

/// <summary>
/// Seeded random walks over the meeting and schedule state machine on one
/// long-lived deployment (database, platform and Agent server survive host
/// restarts): "meeting now", meeting cards and their Refresh / Start-now
/// buttons, schedules created, fired by a due-time warp and cancelled,
/// members joining and leaving two voice rooms, Agent room_started /
/// room_ended / room_summary_done (with transcript 503s), and faults: a
/// server-side socket close, a dropped Agent stream and a host restart.
/// The Agent behaves like the real one (a room ends before the next one
/// starts in the same voice room) and no event is published while no
/// stream is open, so known defects DEF-08 / DEF-12 stay out of the way.
/// After each walk the run settles, the E2E oracles (all but the
/// per-input response count, which the walk does not declare) and
/// MonzeInvariants must hold, and every outbox message was sent at most
/// once. 60 walks times MONZE_PBT_SCALE, seeded by MONZE_CAMPAIGN_SEED.
/// </summary>
public sealed class MeetingWalkTests(ITestOutputHelper output)
{
    private const int BaseWalks = 60;
    private const int OracleEvery = 6;
    private const long SecondVoiceId = 1_840_000_000_000_006_104L;
    private static readonly long[] Voices = [VoiceId, SecondVoiceId];
    private static readonly long[] Members = [OwnerId, AdminId, MemberId, Member2Id];
    private static readonly string[] AllowedWarnings = ["Mezon socket disconnected", "did not get an ack"];
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    [DbFact]
    [Trait("Speed", "Slow")]
    [Req("REQ-MTG-001", "REQ-MTG-002", "REQ-MTG-003", "REQ-MTG-004", "REQ-MTG-005", "REQ-PBT-001")]
    public async Task Random_meeting_walks_keep_the_invariants()
    {
        var seed = RunSeed.For("E2E-MEETING-WALK");
        var random = RunSeed.RandomFor("E2E-MEETING-WALK");
        var walks = BaseWalks * CampaignEnvironment.PbtScale;
        var world = AreaWorld.Create();
        world.AddChannel(ClanId, SecondVoiceId, "Phòng hai", (int)ChannelType.MezonVoice);
        await using var http = await E2EActions.HttpAsync();
        var first = await StartAsync(http, world, null);
        var deployment = new Deployment(http, first);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            deployment.Mark = await E2EOracles.MarkAsync(deployment.Host);
            for (var walk = 0; walk < walks; walk++)
            {
                var state = new WalkState();
                var steps = random.Next(8, 13);
                var trace = new List<string>();
                try
                {
                    for (var step = 0; step < steps; step++)
                    {
                        var name = await StepAsync(deployment, state, random, trace);
                        counts[name] = counts.GetValueOrDefault(name) + 1;
                    }

                    await CloseRoomsAsync(deployment, state, trace);
                    await SettleAsync(deployment.Host);
                    await E2EOracles.AssertInvariantsAsync(deployment.Host);
                    Assert.Equal(0L, await deployment.Host.ScalarAsync<long>(
                        "SELECT count(*) FROM (SELECT external_message_id FROM outbox_delivery WHERE external_message_id IS NOT NULL GROUP BY external_message_id HAVING count(*) > 1) duplicate;"));

                    // The output oracles cover everything since the mark; checking them every few walks keeps the run short.
                    if (walk % OracleEvery == OracleEvery - 1 || walk == walks - 1)
                    {
                        await E2EOracles.AssertAsync(deployment.Host, deployment.Mark, new ScenarioExpectation { ResponsesUnchecked = true, AllowedWarnings = AllowedWarnings });
                        deployment.Mark = await E2EOracles.MarkAsync(deployment.Host);
                    }
                }
                catch (Exception ex) when (ex is not XunitException)
                {
                    throw new XunitException($"seed={seed} walk={walk} steps=[{string.Join("; ", trace)}]: {ex}");
                }
                catch (XunitException ex)
                {
                    throw new XunitException($"seed={seed} walk={walk} steps=[{string.Join("; ", trace)}]: {ex.Message}");
                }
            }
        }
        catch
        {
            await deployment.DisposeAsync();
            throw;
        }

        // Rows parked behind an uncertain parent (WF-02) and lost sends (CAND-21), for the report.
        var blocked = await deployment.Host.ScalarAsync<long>("SELECT count(*) FROM outbox_delivery item WHERE status = 'pending' AND EXISTS (SELECT 1 FROM outbox_delivery parent WHERE parent.id = item.depends_on_id AND parent.status = 'uncertain');");
        var uncertain = await deployment.Host.ScalarAsync<long>("SELECT count(*) FROM outbox_delivery WHERE status = 'uncertain';");
        await deployment.DisposeAsync();
        output.WriteLine($"seed={seed} walks={walks} uncertain-outbox={uncertain} blocked-children={blocked}");
        foreach (var (name, count) in counts.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"  {name}: {count}");
        }
    }

    private static async Task<string> StepAsync(Deployment deployment, WalkState state, Random random, List<string> trace)
    {
        var host = deployment.Host;
        var actor = Members[random.Next(Members.Length)];
        var roll = random.Next(100);
        string name;
        switch (roll)
        {
            case < 12:
                name = "meeting-now";
                await CommandAsync(host, actor, "*meeting now");
                break;
            case < 22:
            {
                name = "meeting-card";
                var list = await CommandAsync(host, actor, "*meeting");
                var card = host.Recorder.Since(list.Sequence).FirstOrDefault(static action => action.Kind == SimActionKind.SendEphemeral);
                if (card is not null)
                {
                    // The card is bound to its user once the send was acked: click after the command finished.
                    await host.WaitForCommandStatusAsync(ClanId, GeneralId, list.MessageId, E2EOracles.Timeout);
                    var button = random.Next(2) == 0 ? MeetingButtonId.Refresh : MeetingButtonId.StartNow;
                    name += "-" + button;
                    await E2EActions.ClickAsync(host, GeneralId, card.MessageId, actor, button);
                }

                break;
            }

            case < 32:
            {
                name = "schedule-create";
                var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddHours(random.Next(2, 48)), Vietnam);
                var kind = new[] { "once", "daily", "weekly" }[random.Next(3)];
                await CommandAsync(host, actor, $"*meeting Walk{random.Next(100)} {local:dd/MM/yyyy} {local:HH}:{random.Next(4) * 15:00} {kind}");
                break;
            }

            case < 40:
            {
                name = "schedule-fire";
                var fired = await host.ScalarAsync<long?>(
                    "UPDATE meeting_schedule SET next_run_at = now() - interval '1 second' WHERE id = (SELECT id FROM meeting_schedule WHERE status = 'active' ORDER BY random() LIMIT 1) RETURNING id;");
                if (fired is long id)
                {
                    await E2EActions.WaitUntilAsync(
                        host,
                        async () => await host.ScalarAsync<long>("SELECT count(*) FROM meeting_schedule WHERE id = @id AND (status IN ('completed', 'cancelled') OR (status = 'active' AND next_run_at > now()));", ("id", id)) == 1,
                        $"schedule {id} to fire");
                }

                await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
                break;
            }

            case < 46:
            {
                name = "schedule-cancel";
                var id = await host.ScalarAsync<long?>("SELECT id FROM meeting_schedule ORDER BY random() LIMIT 1;");
                await CommandAsync(host, actor, $"*meeting cancel {(id ?? 1).ToString(CultureInfo.InvariantCulture)}");
                break;
            }

            case < 58:
            {
                var voice = Voices[random.Next(Voices.Length)];
                var member = Members[random.Next(Members.Length)];
                if (host.World.VoiceOccupants(voice).Contains(member))
                {
                    name = "voice-leave";
                    await host.Inbound.VoiceLeaveAsync(ClanId, voice, member);
                }
                else
                {
                    name = "voice-join";
                    await host.Inbound.VoiceJoinAsync(ClanId, voice, member);
                }

                await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5));
                break;
            }

            case < 92:
            {
                // The Agent: summarize an ended room, end a live one, or start one.
                var voice = Voices[random.Next(Voices.Length)];
                if (state.Ended.Count > 0 && random.Next(2) == 0)
                {
                    name = "agent-summary";
                    var room = state.Ended[random.Next(state.Ended.Count)];
                    state.Ended.Remove(room);
                    await SummarizeAsync(deployment, room, failures: random.Next(3) == 0 ? 3 : 0);
                }
                else if (state.Live.TryGetValue(voice, out var live))
                {
                    name = "agent-ended";
                    await EndAsync(deployment, state, voice, live);
                }
                else
                {
                    name = "agent-started";
                    // Letters only: a long digit run in the room id would show up in the transcript link.
                    var room = "walk-room-" + new string(Enumerable.Range(0, 12).Select(_ => (char)('a' + random.Next(26))).ToArray());
                    await deployment.Http.PublishAgentEventAsync("room_started", room, voice, ClanId);
                    await WaitForAsync(host, room, "'live', 'summary_pending'");
                    state.Live[voice] = room;
                }

                break;
            }

            case < 95:
                name = "socket-close";
                await host.Inbound.CloseSocketAsync();
                await E2EActions.WaitUntilAsync(host, () => Task.FromResult(host.OwnSessions.Any(static session => session.IsConnected && session.HasJoinedClan(ClanId))), "the reconnect");
                break;
            case < 96:
            {
                name = "agent-stream-drop";
                var connections = deployment.Http.SseConnections;
                deployment.Http.DropSseStreams();
                await deployment.Http.WaitForSseAsync(connections + 1, TimeSpan.FromSeconds(15));
                break;
            }

            case < 97:
                name = "restart";
                await deployment.RestartAsync();
                break;
            default:
                name = "help";
                await CommandAsync(host, actor, "*monze help");
                break;
        }

        trace.Add(name);
        return name;
    }

    /// <summary>The Agent ends every live room and summarizes every ended one, as it would.</summary>
    private static async Task CloseRoomsAsync(Deployment deployment, WalkState state, List<string> trace)
    {
        foreach (var (voice, room) in state.Live.ToList())
        {
            await EndAsync(deployment, state, voice, room);
            trace.Add("close-ended");
        }

        foreach (var room in state.Ended.ToList())
        {
            await SummarizeAsync(deployment, room, failures: 0);
            trace.Add("close-summary");
        }

        state.Ended.Clear();
    }

    private static async Task EndAsync(Deployment deployment, WalkState state, long voice, string room)
    {
        await deployment.Http.PublishAgentEventAsync("room_ended", room, voice, ClanId);
        await WaitForAsync(deployment.Host, room, "'summary_pending', 'posted', 'summary_failed'");
        state.Live.Remove(voice);
        state.Ended.Add(room);
    }

    private static async Task SummarizeAsync(Deployment deployment, string room, int failures)
    {
        deployment.Http.SetSummary(room, SimHttpHost.SummaryJson(room, $"Tóm tắt {room[^6..]}."));
        if (failures > 0)
        {
            deployment.Http.Faults.Status(SimHttpRoute.TranscriptSummary, 503, failures);
        }

        await deployment.Http.PublishAgentEventAsync("room_summary_done", room, null, ClanId);

        // A failed fetch leaves the retry to the maintenance worker, whose
        // backoff may still run from earlier empty fetches: warp it due.
        var host = deployment.Host;
        await E2EActions.WaitUntilAsync(
            host,
            async () =>
            {
                await host.ScalarAsync<int>("UPDATE meeting_session SET summary_next_attempt_at = now() WHERE room_id = @room AND status = 'summary_pending' AND summary_next_attempt_at > now() RETURNING 1;", ("room", room));
                return await host.ScalarAsync<long>("SELECT count(*) FROM meeting_session WHERE room_id = @room AND status = 'posted';", ("room", room)) == 1;
            },
            $"room {room} to be posted");
    }

    private static Task WaitForAsync(MonzeE2EHost host, string room, string statuses)
        => E2EActions.WaitUntilAsync(
            host,
            async () => await host.ScalarAsync<long>($"SELECT count(*) FROM meeting_session WHERE room_id = @room AND status IN ({statuses});", ("room", room)) == 1,
            $"room {room} in ({statuses})");

    private static async Task<SimPush> CommandAsync(MonzeE2EHost host, long actor, string text)
    {
        // Every walk command answers in the channel; the answer ends the command.
        var push = await host.Inbound.SayAsync(ClanId, GeneralId, actor, text);
        await host.Recorder.WaitForAsync(static action => action.ChannelId == GeneralId && action.Kind is SimActionKind.SendMessage or SimActionKind.SendEphemeral, E2EOracles.Timeout, push.Sequence);
        await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5));
        return push;
    }

    /// <summary>No outbox send, schedule lease or summary retry still in progress.</summary>
    private static async Task SettleAsync(MonzeE2EHost host)
    {
        const string Pending = """
            SELECT 'outbox ' || id || ' ' || status || ' ' || kind FROM outbox_delivery item
            WHERE status IN ('pending', 'sending') AND due_at <= now() + interval '1 second'
              AND NOT EXISTS (SELECT 1 FROM outbox_delivery parent WHERE parent.id = item.depends_on_id AND parent.status = 'uncertain')
            UNION ALL SELECT 'schedule ' || id || ' running' FROM meeting_schedule WHERE status = 'running'
            UNION ALL SELECT 'session ' || id || ' summary_pending attempts ' || summary_attempts || ' next ' || COALESCE((summary_next_attempt_at - now())::text, 'null') || ' room ' || COALESCE(room_id, 'null') FROM meeting_session WHERE status = 'summary_pending';
            """;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var pending = await host.RowsAsync(Pending);
            if (pending.Count == 0)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"The run did not settle: {string.Join("; ", pending.Select(static row => row[0]))}{Environment.NewLine}{host.Logs.Describe(40)}");
            }

            await Task.Delay(50);
        }
    }

    private static Task<MonzeE2EHost> StartAsync(SimHttpHost http, SimWorld world, MonzeE2EHost? previous)
        => E2EActions.StartAsync(
            previous is null ? "gen_walk" : "gen_walk_restart",
            world,
            http: http,
            ai: false,
            database: previous?.Database,
            simulator: previous?.Simulator,
            dataDirectory: previous?.DataDirectory);

    private sealed class WalkState
    {
        /// <summary>Voice room to its live Agent room.</summary>
        public Dictionary<long, string> Live { get; } = [];

        /// <summary>Agent rooms that ended and wait for their summary.</summary>
        public List<string> Ended { get; } = [];
    }

    /// <summary>The deployment: the HTTP fake and the current host; a restart keeps database, platform and data.</summary>
    private sealed class Deployment(SimHttpHost http, MonzeE2EHost host) : IAsyncDisposable
    {
        private readonly List<MonzeE2EHost> _stopped = [];

        public SimHttpHost Http { get; } = http;

        public MonzeE2EHost Host { get; private set; } = host;

        /// <summary>Start of the current walk's oracle phase on the current host.</summary>
        public E2EMark Mark { get; set; } = new(0, 0, null);

        /// <summary>Checks the old host's part of the walk, stops it and starts a new host on the same deployment.</summary>
        public async Task RestartAsync()
        {
            await E2EOracles.AssertAsync(Host, Mark, new ScenarioExpectation { ResponsesUnchecked = true, AllowedWarnings = AllowedWarnings });
            var connections = Http.SseConnections;
            await Host.StopHostAsync();
            _stopped.Add(Host);
            Host = await StartAsync(Http, Host.World, Host);
            await Http.WaitForSseAsync(connections + 1, TimeSpan.FromSeconds(15));
            Mark = await E2EOracles.MarkAsync(Host);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            for (var i = _stopped.Count - 1; i >= 0; i--)
            {
                await _stopped[i].DisposeAsync();
            }
        }
    }
}
