using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using Xunit.Abstractions;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Workflows;

/// <summary>
/// Two Monze instances on one database, one platform and one bot token (a
/// rolling deploy or an accidental double start). Every command reaches both
/// sockets and is answered exactly once (command_inbox claim); both Agent
/// streams get every event and it is applied once (Agent inbox claim); the
/// pending-summary retry is claimed by one instance under its lease while
/// the other polls, and each summary message is sent once (outbox claim).
/// </summary>
public sealed class TwoInstanceWorkflowTests(ITestOutputHelper output)
{
    [DbFact]
    [Req("REQ-INBOX-001", "REQ-MTG-003", "REQ-MTG-004", "REQ-OUT-001")]
    public async Task Two_instances_answer_each_command_once_and_post_one_summary()
    {
        await using var http = await E2EActions.HttpAsync();
        await using var a = await E2EActions.StartAsync("wf_two_a", http: http);
        await using var b = await E2EActions.StartAsync("wf_two_b", http: http, database: a.Database, simulator: a.Simulator);
        await http.WaitForSseAsync(2, E2EOracles.Timeout);
        Assert.Equal(2, http.OpenSseStreams);

        var markA = await E2EOracles.MarkAsync(a);
        var markB = await E2EOracles.MarkAsync(b);
        var commands = new (long User, string Text, ResponseKind Kind)[]
        {
            (MemberId, "*welcome on", ResponseKind.Reply),
            (MemberId, "*monze help", ResponseKind.Ephemeral),
            (OwnerId, "*welcome on", ResponseKind.Ephemeral),
            (MemberId, "*ai translate chào buổi sáng", ResponseKind.EditedReply),
            (Member2Id, "*monze welcome", ResponseKind.Ephemeral),
            (MemberId, "*meeting now", ResponseKind.Public)
        };
        var inputs = new List<InputExpectation>();
        foreach (var (user, text, kind) in commands)
        {
            var push = await E2EActions.CommandAnySessionAsync(a, GeneralId, user, text);
            Assert.Equal(2, push.DeliveredSessions);
            inputs.Add(new(push, kind));
        }

        await E2EOracles.AssertAsync(a, markA, new ScenarioExpectation { Inputs = inputs });
        await E2EOracles.AssertAsync(b, markB, new ScenarioExpectation { Inputs = inputs });
        var answeredBy = a.Recorder.Since(markA.Sequence).Where(static action => action.IsOutbound && action.Kind is SimActionKind.SendMessage or SimActionKind.SendEphemeral).GroupBy(static action => action.SessionId).ToDictionary(static group => group.Key, static group => group.Count());
        output.WriteLine($"answers per session: {string.Join(", ", answeredBy.Select(static pair => $"s{pair.Key}={pair.Value}"))}");
        var invitation = E2EActions.NewMessageAfter(a, inputs[^1].Input, GeneralId);

        // The Agent cycle reaches both streams; the summary fetch is slow and
        // fails three times first, so both maintenance workers compete for it.
        var phaseA = await E2EOracles.MarkAsync(a);
        var phaseB = await E2EOracles.MarkAsync(b);
        const string room = "sim-room-two-instances";
        Assert.Equal(2, await http.PublishAgentEventAsync("room_started", room, VoiceId, ClanId));
        await MeetingAgentWorkflowTests.WaitForStatusAsync(a, room, "live");
        Assert.Equal(2, await http.PublishAgentEventAsync("room_ended", room, VoiceId, ClanId));
        await MeetingAgentWorkflowTests.WaitForStatusAsync(a, room, "summary_pending");
        http.SetSummary(room, SimHttpHost.SummaryJson(room, "Hai instance, một bản tóm tắt."));
        http.Faults.Delay(SimHttpRoute.TranscriptSummary, TimeSpan.FromMilliseconds(1500), times: int.MaxValue).Status(SimHttpRoute.TranscriptSummary, 503, times: 3);
        Assert.Equal(2, await http.PublishAgentEventAsync("room_summary_done", room, VoiceId, ClanId));
        await MeetingAgentWorkflowTests.SummaryPairAsync(a, room, phaseA.Sequence, invitation.MessageId);
        http.Faults.Clear();

        await E2EOracles.AssertAsync(a, phaseA, new ScenarioExpectation { OtherOutputs = 4 });
        await E2EOracles.AssertAsync(b, phaseB, new ScenarioExpectation { OtherOutputs = 4 });
        // Three failed fetches, then success; a maintenance poll right after room_ended may add a 404.
        var statuses = http.Requests.Where(static request => request.Route == SimHttpRoute.TranscriptSummary).Select(static request => request.Status).ToList();
        output.WriteLine($"transcript fetches: {string.Join(", ", statuses)}");
        Assert.Equal(3, statuses.Count(static status => status == 503));
        Assert.Equal(200, statuses[^1]);
        Assert.All(statuses, static status => Assert.Contains(status, new[] { 404, 503, 200 }));
        // One inbox claim per Agent event, although each reached both instances.
        Assert.Equal(3L, await a.ScalarAsync<long>("SELECT count(*) FROM inbox_event WHERE source = 'agent';"));
        Assert.Equal(2L, await a.ScalarAsync<long>("SELECT count(DISTINCT external_message_id) FROM outbox_delivery WHERE kind = 'MeetingSummary' AND status = 'sent';"));
        await E2EOracles.AssertInvariantsAsync(a);
    }
}
