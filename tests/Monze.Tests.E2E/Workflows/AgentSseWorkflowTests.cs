using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Workflows;

/// <summary>
/// The Agent SSE stream under faults (Mezon.Net.Sdk 1.6.2 AgentSseManager,
/// Monze's Agent ingress): a refused first connect and a dropped stream are
/// followed by reconnects and later events are processed; duplicate,
/// reordered, malformed, empty, oversized and out-of-scope events change the
/// meeting state once and only where they belong. Monze reads the stream
/// through its own HTTP pipeline (AgentEventStream), which resumes with
/// Last-Event-ID and replaces a half-open stream (regression for DEF-08).
/// </summary>
public sealed class AgentSseWorkflowTests
{
    [DbFact]
    [Req("REQ-MTG-003", "REQ-CONN-001")]
    public async Task A_refused_connect_and_a_dropped_stream_are_followed_by_reconnects()
    {
        await using var http = await E2EActions.HttpAsync();
        http.Faults.Status(SimHttpRoute.AgentSse, 503);
        await using var host = await E2EActions.StartAsync("wf_sse_reconnect", http: http);
        await http.WaitForSseAsync(1, TimeSpan.FromSeconds(15));
        Assert.Equal(new[] { 503, 200 }, http.Requests.Where(static request => request.Route == SimHttpRoute.AgentSse).Select(static request => request.Status));
        await MeetingAgentWorkflowTests.MeetingNowAsync(host);

        var phase = await E2EOracles.MarkAsync(host);
        const string room = "sim-room-reconnect";
        await http.PublishAgentEventAsync("room_started", room, VoiceId, ClanId);
        await MeetingAgentWorkflowTests.WaitForStatusAsync(host, room, "live");
        Assert.Equal(1, http.DropSseStreams());
        await http.WaitForSseAsync(2, TimeSpan.FromSeconds(15));
        Assert.Equal(1, await http.PublishAgentEventAsync("room_ended", room, VoiceId, ClanId));
        await MeetingAgentWorkflowTests.WaitForStatusAsync(host, room, "summary_pending");

        // Nothing was seen before the first open; the reconnect resumes after room_started.
        Assert.Equal(new[] { false, false, true }, http.Requests.Where(static request => request.Route == SimHttpRoute.AgentSse).Select(static request => request.HadLastEventId));
        await E2EOracles.AssertAsync(host, phase, new ScenarioExpectation { OtherOutputs = 2 });
        await E2EOracles.AssertInvariantsAsync(host);
    }

    /// <summary>
    /// Regression for DEF-08: the SDK's AgentSseManager reads with an
    /// infinite HttpClient timeout and no idle detection (Mezon.Net.Sdk 1.6.2
    /// src/Mezon.Net.Sdk/Agent/AgentSseManager.cs ReadOnceAsync) and never
    /// sends Last-Event-ID, so a half-open stream (the server keeps sending
    /// keepalives every second, none arrive) used to lose every Agent event
    /// until a restart. Monze's pipeline ends a stream that has sent
    /// keepalives and then stays silent for three keepalive gaps (at least
    /// Mezon:AgentSse:IdleTimeoutSeconds, 2 here): within 10 s a new stream
    /// is opened with Last-Event-ID and the missed room_ended is replayed and
    /// processed. A stream that goes silent before its second keepalive is
    /// not timed out (the live server's keepalive interval is unknown).
    /// </summary>
    [DbFact]
    [Req("REQ-MTG-003", "REQ-CONN-001")]
    public async Task A_half_open_stream_is_replaced_and_resumed_after_the_last_event()
    {
        await using var http = await E2EActions.HttpAsync();
        await using var host = await E2EActions.StartAsync("wf_sse_halfopen", http: http, configuration: new Dictionary<string, string?>
        {
            ["Mezon:AgentSse:IdleTimeoutSeconds"] = "2"
        });
        await http.WaitForSseAsync(1, E2EOracles.Timeout);
        await MeetingAgentWorkflowTests.MeetingNowAsync(host);

        var phase = await E2EOracles.MarkAsync(host);
        const string room = "sim-room-half-open";
        await http.PublishAgentEventAsync("room_started", room, VoiceId, ClanId);
        await MeetingAgentWorkflowTests.WaitForStatusAsync(host, room, "live");

        // A healthy stream first: the fake sends a keepalive after each idle second.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Equal(1, http.HangSseStreams());
        Assert.Equal(0, await http.PublishAgentEventAsync("room_ended", room, VoiceId, ClanId));

        await http.WaitForSseAsync(2, TimeSpan.FromSeconds(10));
        Assert.True(http.Requests.Last(static request => request.Route == SimHttpRoute.AgentSse).HadLastEventId);
        await MeetingAgentWorkflowTests.WaitForStatusAsync(host, room, "summary_pending");

        await E2EOracles.AssertAsync(host, phase, new ScenarioExpectation { OtherOutputs = 2 });
        await E2EOracles.AssertInvariantsAsync(host);
    }

    [DbFact]
    [Req("REQ-MTG-003", "REQ-MTG-004")]
    public async Task Duplicate_reordered_malformed_foreign_oversized_and_empty_events_apply_once()
    {
        await using var http = await E2EActions.HttpAsync();
        await using var host = await E2EActions.StartAsync("wf_sse_faults", http: http);
        await http.WaitForSseAsync(1, E2EOracles.Timeout);
        var invitation = await MeetingAgentWorkflowTests.MeetingNowAsync(host);

        // Room A: room_ended overtakes room_started; room_summary_done arrives twice.
        var phaseA = await E2EOracles.MarkAsync(host);
        const string roomA = "sim-room-reordered";
        Assert.Equal(0, await http.PublishAgentEventAsync("room_started", roomA, VoiceId, ClanId, delivery: SimSseDelivery.Hold));
        Assert.Equal(1, await http.PublishAgentEventAsync("room_ended", roomA, VoiceId, ClanId));
        await MeetingAgentWorkflowTests.WaitForStatusAsync(host, roomA, "summary_pending");
        http.SetSummary(roomA, SimHttpHost.SummaryJson(roomA, "Phòng A đã chốt việc."));
        await http.PublishAgentEventAsync("room_summary_done", roomA, VoiceId, ClanId, delivery: SimSseDelivery.Duplicate);
        await MeetingAgentWorkflowTests.SummaryPairAsync(host, roomA, phaseA.Sequence, invitation.MessageId);
        await E2EOracles.AssertAsync(host, phaseA, new ScenarioExpectation { OtherOutputs = 3 });
        // The duplicated frame is accepted once (the Agent inbox); the maintenance worker may also poll the transcript.
        Assert.Single(host.Logs.Entries.Skip(phaseA.LogCount), static entry => entry.Message.StartsWith("Agent event accepted. Kind=SummaryDone", StringComparison.Ordinal));
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM agent_event WHERE room_id = @room;", ("room", roomA)));

        // Junk on the stream: malformed JSON, a frame without data, an
        // oversized unknown event, events for a clan without the bot and for
        // another clan's voice room. None may change anything.
        var phaseJunk = await E2EOracles.MarkAsync(host, snapshot: true);
        await http.PublishRawAsync("data: {not json\n\nevent: room_started\ndata: [1,2\n\nevent: room_started\ndata:\n\n: comment only\n\n");
        await http.PublishJsonAsync("room_heartbeat", "{\"event_type\":\"room_heartbeat\",\"pad\":\"" + new string('x', 1024 * 1024) + "\"}");
        await http.PublishAgentEventAsync("room_started", "sim-room-foreign-clan", LateGeneralId, LateClanId);
        await http.PublishAgentEventAsync("room_started", "sim-room-wrong-clan", VoiceId, OtherClanId);
        await http.PublishAgentEventAsync("room_started", "sim-room-text-channel", GeneralId, ClanId);

        // Valid JSON that is not an object (CAND-01, G17): AgentEventPayload.TryParse
        // only catches JsonException, so the ingress worker logs a warning with
        // an InvalidOperationException for each (and carries on).
        await http.PublishJsonAsync("room_started", "[1,2]");
        await http.PublishJsonAsync("room_ended", "\"room\"");
        await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        await E2EOracles.AssertAsync(host, phaseJunk, new ScenarioExpectation { Unauthorized = true, AllowedWarnings = ["Agent event worker failed."] });
        KnownDefect.ExpectFailure("CAND-01", () => Assert.DoesNotContain(host.Logs.Entries, static entry => entry.Message.StartsWith("Agent event worker failed", StringComparison.Ordinal)));

        // Room B after the junk: started arrives twice; the stream is still healthy.
        var phaseB = await E2EOracles.MarkAsync(host);
        const string roomB = "sim-room-duplicated-start";
        await http.PublishAgentEventAsync("room_started", roomB, VoiceId, ClanId, delivery: SimSseDelivery.Duplicate);
        await MeetingAgentWorkflowTests.WaitForStatusAsync(host, roomB, "live");
        await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        await E2EOracles.AssertAsync(host, phaseB, new ScenarioExpectation { OtherOutputs = 1 });
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM meeting_session WHERE room_id = @room;", ("room", roomB)));
        Assert.Equal(1, http.SseConnections);
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM meeting_session WHERE room_id LIKE 'sim-room-%clan' OR room_id = 'sim-room-text-channel';"));
        await E2EOracles.AssertInvariantsAsync(host);
    }
}
