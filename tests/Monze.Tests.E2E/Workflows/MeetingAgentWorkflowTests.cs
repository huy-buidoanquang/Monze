using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Workflows;

/// <summary>
/// Meetings end to end with the Agent over SSE (SimHttpHost): "meeting now"
/// posts the invitation and a suggested session; room_started makes it live
/// and edits the invitation into the Agent status; room_ended moves it to
/// summary_pending; room_summary_done fetches the transcript and posts the
/// summary and its action items, in that order, as replies to the
/// invitation. A second Agent cycle in the same voice room becomes a child
/// session that posts its own pair; a transcript answering 503 three times is
/// retried by the maintenance worker; eight failed retries end in
/// summary_failed with one failure notice. DEF-12 (a lost room_ended leaves
/// the session live forever) is checked as a known defect.
/// </summary>
public sealed class MeetingAgentWorkflowTests
{
    [DbFact]
    [Req("REQ-MTG-001", "REQ-MTG-003", "REQ-MTG-004", "REQ-OUT-001")]
    [Covers("msg:MeetingAgentInstruction")]
    public async Task Meeting_now_and_two_agent_cycles_post_ordered_summaries()
    {
        await using var http = await E2EActions.HttpAsync();
        await using var host = await E2EActions.StartAsync("wf_meeting_cycles", http: http);
        await http.WaitForSseAsync(1, E2EOracles.Timeout);

        var invitation = await MeetingNowAsync(host);
        var sessionId = await host.ScalarAsync<long>("SELECT id FROM meeting_session WHERE clan_id = @clan AND status = 'suggested';", ("clan", ClanId));

        // Cycle 1: started -> live -> ended -> summary_pending -> transcript -> posted.
        var cycle1 = await E2EOracles.MarkAsync(host);
        await host.Inbound.VoiceJoinAsync(ClanId, VoiceId, MemberId);
        const string room1 = "sim-room-cycle-1";
        Assert.Equal(1, await http.PublishAgentEventAsync("room_started", room1, VoiceId, ClanId));
        await WaitForStatusAsync(host, room1, "live");
        Assert.Equal(sessionId, await host.ScalarAsync<long>("SELECT id FROM meeting_session WHERE room_id = @room;", ("room", room1)));
        var live = await host.Recorder.WaitForAsync(action => action.Kind == SimActionKind.UpdateMessage && action.MessageId == invitation.MessageId, E2EOracles.Timeout, cycle1.Sequence);
        await http.PublishAgentEventAsync("room_ended", room1, VoiceId, ClanId);
        await WaitForStatusAsync(host, room1, "summary_pending");
        await host.Recorder.WaitForAsync(action => action.Kind == SimActionKind.UpdateMessage && action.MessageId == invitation.MessageId, E2EOracles.Timeout, live.Sequence);
        http.SetSummary(room1, SimHttpHost.SummaryJson(
            room1,
            "Nhóm thống nhất kế hoạch phát hành.",
            new Dictionary<string, string[]> { [MemberId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = ["Viết ghi chú phát hành"] },
            [(MemberId.ToString(System.Globalization.CultureInfo.InvariantCulture), 120), (Member2Id.ToString(System.Globalization.CultureInfo.InvariantCulture), 60)]));
        await http.PublishAgentEventAsync("room_summary_done", room1, VoiceId, ClanId);
        var first = await SummaryPairAsync(host, room1, cycle1.Sequence, invitation.MessageId);
        Assert.Equal($"TÓM TẮT HỘI THOẠI #{sessionId} (🔊 {VoiceLabel})", E2EContent.Parse(first.Summary).Embeds![0].Title);
        Assert.Contains("Nhóm thống nhất kế hoạch phát hành.", E2EContent.Visible(first.Summary));
        Assert.Contains("Viết ghi chú phát hành", E2EContent.Visible(first.Actions));
        await E2EOracles.AssertAsync(host, cycle1, new ScenarioExpectation { OtherOutputs = 4 });

        // Cycle 2 in the same room: a child session; the transcript answers 503 three times first.
        var cycle2 = await E2EOracles.MarkAsync(host);
        const string room2 = "sim-room-cycle-2";
        await http.PublishAgentEventAsync("room_started", room2, VoiceId, ClanId);
        await WaitForStatusAsync(host, room2, "live");
        var child = Assert.Single(await host.RowsAsync("SELECT id, root_session_id FROM meeting_session WHERE room_id = @room;", ("room", room2)));
        Assert.Equal(sessionId, child[1]);
        await http.PublishAgentEventAsync("room_ended", room2, VoiceId, ClanId);
        await WaitForStatusAsync(host, room2, "summary_pending");
        http.SetSummary(room2, SimHttpHost.SummaryJson(room2, "Vòng hai: rà soát lỗi."));
        http.Faults.Status(SimHttpRoute.TranscriptSummary, 503, times: 3);
        await http.PublishAgentEventAsync("room_summary_done", room2, VoiceId, ClanId);
        var second = await SummaryPairAsync(host, room2, cycle2.Sequence, invitation.MessageId);
        Assert.Equal($"TÓM TẮT HỘI THOẠI #{child[0]} (🔊 {VoiceLabel})", E2EContent.Parse(second.Summary).Embeds![0].Title);
        Assert.Contains("Chưa có đầu mục công việc.", E2EContent.Visible(second.Actions));
        var fetches = http.Requests.Where(static request => request.Route == SimHttpRoute.TranscriptSummary && request.Path.EndsWith(room2, StringComparison.Ordinal)).ToList();
        // The maintenance worker may poll the transcript as soon as the room ended (404 before the
        // summary exists, or a second 200 racing the Agent path); the three 503s come before the success.
        var statuses = fetches.Select(static request => request.Status).ToList();
        Assert.Equal(3, statuses.Count(static status => status == 503));
        Assert.Equal(200, statuses[^1]);
        Assert.True(statuses.IndexOf(503) < statuses.LastIndexOf(200));
        Assert.All(statuses, static status => Assert.Contains(status, new[] { 404, 503, 200 }));
        await host.Inbound.VoiceLeaveAsync(ClanId, VoiceId, MemberId);
        await E2EOracles.AssertAsync(host, cycle2, new ScenarioExpectation { OtherOutputs = 4 });

        Assert.Equal(new[] { "posted", "posted" }, (await host.RowsAsync("SELECT status FROM meeting_session WHERE clan_id = @clan ORDER BY id;", ("clan", ClanId))).Select(static row => (string)row[0]!));
        Assert.Equal(4L, await host.ScalarAsync<long>("SELECT count(*) FROM outbox_delivery WHERE kind = 'MeetingSummary' AND status = 'sent';"));
        Assert.Empty(http.Unmodelled);
        await E2EOracles.AssertInvariantsAsync(host);
    }

    [DbFact]
    [Req("REQ-MTG-004", "REQ-MTG-006")]
    [Covers("msg:SummaryFailed")]
    public async Task Eight_failed_transcript_retries_end_in_summary_failed_with_one_notice()
    {
        await using var http = await E2EActions.HttpAsync();
        await using var host = await E2EActions.StartAsync("wf_meeting_fail", http: http);
        await http.WaitForSseAsync(1, E2EOracles.Timeout);
        var invitation = await MeetingNowAsync(host);

        var phase = await E2EOracles.MarkAsync(host);
        const string room = "sim-room-failing";
        await http.PublishAgentEventAsync("room_started", room, VoiceId, ClanId);
        await WaitForStatusAsync(host, room, "live");
        await http.PublishAgentEventAsync("room_ended", room, VoiceId, ClanId);
        await WaitForStatusAsync(host, room, "summary_pending");
        http.Faults.Status(SimHttpRoute.TranscriptSummary, 500, times: int.MaxValue);
        await http.PublishAgentEventAsync("room_summary_done", room, VoiceId, ClanId);

        // Time warp on the campaign database: each retry is due at once
        // instead of after the 5 s .. 300 s backoff.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (await host.ScalarAsync<string>("SELECT status FROM meeting_session WHERE room_id = @room;", ("room", room)) != "summary_failed")
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The summary never reached summary_failed.");
            await host.ScalarAsync<int>(
                "UPDATE meeting_session SET summary_next_attempt_at = now() WHERE room_id = @room AND status = 'summary_pending' AND summary_next_attempt_at > now() RETURNING 1;",
                ("room", room));
            await Task.Delay(100);
        }

        var notice = await host.Recorder.WaitForAsync(
            static action => action.Kind == SimActionKind.SendMessage && action.ChannelId == GeneralId,
            E2EOracles.Timeout,
            phase.Sequence);
        http.Faults.Clear();
        await E2EOracles.AssertAsync(host, phase, new ScenarioExpectation { OtherOutputs = 3 });
        Assert.Equal(invitation.MessageId, notice.ReplyToMessageId);
        Assert.Contains(MonzeMessages.SummaryFailed, E2EContent.Visible(notice));
        var row = Assert.Single(await host.RowsAsync("SELECT summary_attempts, summary_last_error FROM meeting_session WHERE room_id = @room;", ("room", room)));
        Assert.Equal(8, row[0]);
        Assert.Equal("summary-empty", row[1]);
        // Eight failed retries, plus the Agent path's own fetch (one retry may be a 404 that ran before the fault was set).
        Assert.InRange(http.Requests.Count(static request => request.Route == SimHttpRoute.TranscriptSummary && request.Status is 500 or 404), 8, 9);
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM outbox_delivery WHERE dedupe_key LIKE 'meeting-summary-failed:%' AND status = 'sent';"));
        await E2EOracles.AssertInvariantsAsync(host);
    }

    /// <summary>
    /// Regression for DEF-12: when room_ended never arrives, nothing used to
    /// move the session out of 'live' (ExpireSuggestedAsync only expires
    /// suggested and requested sessions, and the voice room emptying only
    /// closes the context), so the next Agent cycle in that room opened a
    /// second live session. A new room in the voice channel now ends the
    /// stale live session (summary_pending, its summary is fetched).
    /// </summary>
    [DbFact]
    [Req("REQ-MTG-003", "REQ-MTG-006")]
    public async Task A_lost_room_ended_is_closed_by_the_next_agent_cycle()
    {
        await using var http = await E2EActions.HttpAsync();
        await using var host = await E2EActions.StartAsync("wf_meeting_lost_end", http: http);
        await http.WaitForSseAsync(1, E2EOracles.Timeout);
        await MeetingNowAsync(host);

        var phase = await E2EOracles.MarkAsync(host);
        const string room = "sim-room-lost-end";
        await host.Inbound.VoiceJoinAsync(ClanId, VoiceId, MemberId);
        await http.PublishAgentEventAsync("room_started", room, VoiceId, ClanId);
        await WaitForStatusAsync(host, room, "live");

        // room_ended is lost in transit; everybody leaves; a day passes.
        await host.Inbound.VoiceLeaveAsync(ClanId, VoiceId, MemberId);
        await E2EActions.WaitUntilAsync(
            host,
            async () => await host.ScalarAsync<DateTime?>("SELECT context_closed_at FROM meeting_session WHERE room_id = @room;", ("room", room)) is not null,
            "the voice room context to close");
        await host.ScalarAsync<int>("UPDATE meeting_session SET started_at = now() - interval '2 days' WHERE room_id = @room RETURNING 1;", ("room", room));
        await Task.Delay(TimeSpan.FromSeconds(3));

        // The Agent starts again in that room.
        const string next = "sim-room-after-lost-end";
        await host.Inbound.VoiceJoinAsync(ClanId, VoiceId, MemberId);
        await http.PublishAgentEventAsync("room_started", next, VoiceId, ClanId);
        await WaitForStatusAsync(host, next, "live");
        await host.Inbound.VoiceLeaveAsync(ClanId, VoiceId, MemberId);

        Assert.Equal("summary_pending", await host.ScalarAsync<string>("SELECT status FROM meeting_session WHERE room_id = @room;", ("room", room)));
        Assert.Equal(1L, await host.ScalarAsync<long>(
            "SELECT count(*) FROM meeting_session WHERE clan_id = @clan AND voice_channel_id = @voice AND status = 'live';",
            ("clan", ClanId),
            ("voice", VoiceId)));

        // Outputs: the status edit of the invitation (cycle 1) and the new
        // direct session's own status message in the voice room's text.
        await E2EOracles.AssertAsync(host, phase, new ScenarioExpectation { OtherOutputs = 2 });
        await E2EOracles.AssertInvariantsAsync(host);
    }

    /// <summary>"*meeting now" with the voice room free: one public invitation, one suggested session.</summary>
    internal static async Task<SimAction> MeetingNowAsync(MonzeE2EHost host)
    {
        var mark = await E2EOracles.MarkAsync(host);
        var now = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*meeting now");
        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of((now, ResponseKind.Public)));
        var invitation = E2EActions.NewMessageAfter(host, now, GeneralId);
        Assert.True(invitation.MentionEveryone);
        Assert.Contains(MonzeMessages.MeetingAgentInstruction, E2EContent.Visible(invitation));
        return invitation;
    }

    internal static Task WaitForStatusAsync(MonzeE2EHost host, string roomId, string status)
        => E2EActions.WaitUntilAsync(
            host,
            async () => await host.ScalarAsync<string>("SELECT status FROM meeting_session WHERE room_id = @room;", ("room", roomId)) == status,
            $"room {roomId} to be '{status}'");

    /// <summary>Waits until both summary messages of the room were sent and returns them in send order.</summary>
    internal static async Task<(SimAction Summary, SimAction Actions)> SummaryPairAsync(MonzeE2EHost host, string roomId, long after, long replyTo)
    {
        await WaitForStatusAsync(host, roomId, "posted");
        await E2EActions.WaitUntilAsync(
            host,
            async () => await host.ScalarAsync<long>(
                "SELECT count(*) FROM outbox_delivery d JOIN meeting_session s ON s.id = d.meeting_session_id WHERE s.room_id = @room AND d.status = 'sent';",
                ("room", roomId)) == 2,
            $"both summary messages of {roomId}");
        var sends = host.Recorder.Since(after)
            .Where(static action => action.Kind == SimActionKind.SendMessage && action.ChannelId == GeneralId)
            .ToList();
        Assert.Equal(2, sends.Count);
        Assert.Equal(replyTo, sends[0].ReplyToMessageId);
        Assert.StartsWith("TÓM TẮT HỘI THOẠI #", E2EContent.Parse(sends[0]).Embeds![0].Title, StringComparison.Ordinal);
        Assert.StartsWith("CÁC ĐẦU MỤC CÔNG VIỆC #", E2EContent.Parse(sends[1]).Embeds![0].Title, StringComparison.Ordinal);
        var external = await host.RowsAsync(
            "SELECT d.external_message_id FROM outbox_delivery d JOIN meeting_session s ON s.id = d.meeting_session_id WHERE s.room_id = @room ORDER BY d.id;",
            ("room", roomId));
        Assert.Equal(new object?[] { sends[0].MessageId, sends[1].MessageId }, external.Select(static row => row[0]));
        return (sends[0], sends[1]);
    }
}
