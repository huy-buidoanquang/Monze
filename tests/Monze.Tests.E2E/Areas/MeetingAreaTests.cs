using System.Globalization;
using System.Text.Json;
using Mezon.Net.Client;
using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Monze.Ui;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Areas;

/// <summary>
/// Meeting: "now" with every room busy says so and suggests the room once it
/// is free; schedules are created once, daily and weekly through the real
/// form buttons and extra_data, listed, and cancelled by button and by
/// command; invalid form input shows the parser's error; two schedules in the
/// same slot give one suggestion while the second waits for a free room.
/// </summary>
public sealed class MeetingAreaTests
{
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    /// <summary>
    /// CAND-27 (decided: tell the creator): a scheduled meeting that finds no
    /// free voice room is postponed by 5 minutes and its requester is told
    /// once, privately in the schedule's channel; a further postponement of
    /// the same occurrence says nothing more.
    /// </summary>
    [DbFact]
    [Req("REQ-MTG-002")]
    [Covers("msg:SchedulePostponed")]
    public async Task A_schedule_with_every_room_busy_tells_its_requester_once()
    {
        var world = AreaWorld.Create();
        world.JoinVoice(VoiceId, Member2Id);
        await using var host = await E2EActions.StartAsync("meeting_schedule_busy", world);
        var id = await host.ScalarAsync<long>(
            "INSERT INTO meeting_schedule(clan_id, channel_id, requester_id, title, kind, when_text, timezone, next_run_at, status) VALUES (@clan, @channel, @user, 'Họp tuần', 'Once', '18:30', 'Asia/Ho_Chi_Minh', now() - interval '1 second', 'active') RETURNING id;",
            ("clan", ClanId),
            ("channel", GeneralId),
            ("user", MemberId));
        await E2EActions.WaitUntilAsync(host, () => Task.FromResult(Notices(host, id) == 1), "the requester's notice");
        Assert.Equal("no-voice-room", await host.ScalarAsync<string>("SELECT last_error FROM meeting_schedule WHERE id = @id;", ("id", id)));

        await host.ScalarAsync<int>("UPDATE meeting_schedule SET next_run_at = now() - interval '1 second' WHERE id = @id RETURNING 1;", ("id", id));
        await E2EActions.WaitUntilAsync(host, async () => await host.ScalarAsync<bool>("SELECT next_run_at > now() FROM meeting_schedule WHERE id = @id;", ("id", id)), "the second postponement");
        await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));

        Assert.Equal(1, Notices(host, id));
    }

    private static int Notices(MonzeE2EHost host, long scheduleId)
        => host.Recorder.Actions.Count(action => action.Kind == SimActionKind.SendEphemeral
            && action.ChannelId == GeneralId
            && action.TargetUserId == MemberId
            && E2EContent.Visible(action).Contains(MonzeMessages.SchedulePostponed("Họp tuần", scheduleId), StringComparison.Ordinal));

    [DbFact]
    [Req("REQ-MTG-001")]
    [Covers("msg:NoVoiceRoom", "msg:MeetingAgentInstruction", "cmd:meeting")]
    public async Task Meeting_now_with_every_room_busy_says_so_and_suggests_the_room_once_free()
    {
        var world = AreaWorld.Create();
        world.JoinVoice(VoiceId, Member2Id);
        await using var host = await E2EActions.StartAsync("meeting_busy", world);
        var mark = await E2EOracles.MarkAsync(host);

        var busy = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*meeting now");
        await host.Inbound.VoiceLeaveAsync(ClanId, VoiceId, Member2Id);
        var free = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*meeting now");

        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of((busy, ResponseKind.Reply), (free, ResponseKind.Public)));
        Assert.Contains(MonzeMessages.NoVoiceRoom, Answer(host, busy));
        var suggestion = E2EActions.NewMessageAfter(host, free, GeneralId);
        Assert.True(suggestion.MentionEveryone);
        Assert.Equal($"@here Mọi người tham gia phòng {VoiceLabel} để bắt đầu cuộc hội thoại.", E2EContent.Parse(suggestion).Text);
        Assert.Contains(MonzeMessages.MeetingAgentInstruction, E2EContent.Visible(suggestion));
        var session = Assert.Single(await host.RowsAsync(
            "SELECT status, voice_channel_id, notification_message_id FROM meeting_session WHERE clan_id = @clan;",
            ("clan", ClanId)));
        Assert.Equal(new object?[] { "suggested", VoiceId, suggestion.MessageId }, session);
    }

    [DbFact]
    [Req("REQ-MTG-001", "REQ-MTG-002")]
    [Covers("btn:monze_meeting_schedule", "btn:monze_meeting_schedule_submit", "btn:monze_meeting_cancel:", "msg:ScheduleSaved", "msg:MeetingScheduleEmpty", "msg:MeetingScheduleCancelled", "msg:MeetingScheduleNotFound")]
    public async Task Schedules_are_made_through_the_form_listed_and_cancelled()
    {
        await using var host = await E2EActions.StartAsync("meeting_form");
        var mark = await E2EOracles.MarkAsync(host);
        var inputs = new List<InputExpectation>();
        var tomorrow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Vietnam).Date.AddDays(1);

        var (list, listCard) = await ListAsync(host, inputs);
        Assert.Contains(MonzeMessages.MeetingScheduleEmpty, E2EContent.Visible(list));
        var form = await E2EActions.ClickAsync(host, GeneralId, listCard, OwnerId, MeetingButtonId.Schedule);
        inputs.Add(new(form, ResponseKind.Update));
        Assert.Contains("Lên lịch meeting", E2EContent.Visible(E2EActions.LastUpdateAfter(host, form, listCard)));
        var invalid = await E2EActions.ClickAsync(host, GeneralId, listCard, OwnerId, MeetingButtonId.ScheduleSubmit,
            Form("Standup", tomorrow.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), "25:99", "once"));
        inputs.Add(new(invalid, ResponseKind.Update));
        Assert.Contains("Giờ phải theo dạng HH:mm.", E2EContent.Visible(E2EActions.LastUpdateAfter(host, invalid, listCard)));
        var once = await E2EActions.ClickAsync(host, GeneralId, listCard, OwnerId, MeetingButtonId.ScheduleSubmit,
            Form("Standup", tomorrow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "09:30", "once"));
        inputs.Add(new(once, ResponseKind.Update));
        Assert.Contains("Đã lưu lịch \"Standup\"", E2EContent.Visible(E2EActions.LastUpdateAfter(host, once, listCard)));

        foreach (var (name, kind) in new[] { ("Daily sync", "daily"), ("Weekly review", "weekly") })
        {
            var (_, card) = await ListAsync(host, inputs);
            inputs.Add(new(await E2EActions.ClickAsync(host, GeneralId, card, OwnerId, MeetingButtonId.Schedule), ResponseKind.Update));
            var submit = await E2EActions.ClickAsync(host, GeneralId, card, OwnerId, MeetingButtonId.ScheduleSubmit,
                Form(name, tomorrow.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), "10:00", kind));
            inputs.Add(new(submit, ResponseKind.Update));
            Assert.Contains($"kiểu {kind}", E2EContent.Visible(E2EActions.LastUpdateAfter(host, submit, card)));
        }

        var schedules = await SchedulesAsync(host);
        Assert.Equal(new[] { "Once", "Daily", "Weekly" }, schedules.Select(static row => (string)row[2]!));
        var (full, fullCard) = await ListAsync(host, inputs);
        var cancelButtons = E2EContent.Buttons(full).Where(static id => id.StartsWith(MeetingButtonId.CancelPrefix, StringComparison.Ordinal)).ToList();

        // The list is ordered by next run, which depends on the time of day
        // (a daily 10:00 schedule runs today before tomorrow's 09:30 one-off).
        var byNextRun = await host.RowsAsync("SELECT id FROM meeting_schedule WHERE clan_id = @clan ORDER BY next_run_at, id;", ("clan", ClanId));
        Assert.Equal(byNextRun.Select(static row => MeetingButtonId.CancelFor((long)row[0]!)), cancelButtons);

        var cancelOnce = await E2EActions.ClickAsync(host, GeneralId, fullCard, OwnerId, MeetingButtonId.CancelFor((long)schedules[0][0]!));
        inputs.Add(new(cancelOnce, ResponseKind.Update));
        Assert.Contains(MonzeMessages.MeetingScheduleCancelled, E2EContent.Visible(E2EActions.LastUpdateAfter(host, cancelOnce, fullCard)));
        var cancelDaily = await E2EActions.CommandAsync(host, GeneralId, OwnerId, $"*meeting cancel {schedules[1][0]}");
        inputs.Add(new(cancelDaily, ResponseKind.Reply));
        Assert.Contains(MonzeMessages.MeetingScheduleCancelled, Answer(host, cancelDaily));
        var cancelMissing = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*meeting cancel 999999");
        inputs.Add(new(cancelMissing, ResponseKind.Reply));
        Assert.Contains(MonzeMessages.MeetingScheduleNotFound, Answer(host, cancelMissing));

        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { Inputs = inputs });
        Assert.Equal(new[] { "cancelled", "cancelled", "active" }, (await SchedulesAsync(host)).Select(static row => (string)row[1]!));
        Assert.Equal(1L, await host.ScalarAsync<long>(
            "SELECT count(*) FROM interaction_inbox WHERE channel_id = @channel AND message_id = @message AND status = 'completed';",
            ("channel", GeneralId),
            ("message", listCard)));
    }

    [DbFact]
    [Req("REQ-MTG-002")]
    [Covers("msg:ScheduleSaved")]
    public async Task Two_schedules_in_one_slot_give_one_suggestion_and_the_second_waits_for_a_room()
    {
        await using var host = await E2EActions.StartAsync("meeting_slot");
        var date = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Vietnam).Date.AddDays(1).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var mark = await E2EOracles.MarkAsync(host);
        var standup = await E2EActions.CommandAsync(host, GeneralId, OwnerId, $"*meeting Standup {date} 09:30 once");
        var retro = await E2EActions.CommandAsync(host, GeneralId, AdminId, $"*meeting Retro {date} 09:30 once");
        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of((standup, ResponseKind.Reply), (retro, ResponseKind.Reply)));
        Assert.Contains("Đã lưu lịch \"Standup\"", Answer(host, standup));
        Assert.Contains("Đã lưu lịch \"Retro\"", Answer(host, retro));

        // Time warp on the campaign database: both schedules become due now.
        var due = await E2EOracles.MarkAsync(host);
        await host.ScalarAsync<int>(
            "UPDATE meeting_schedule SET next_run_at = now() - interval '1 second' WHERE clan_id = @clan RETURNING 1;",
            ("clan", ClanId));
        var suggestion = await host.Recorder.WaitForAsync(
            static action => action.Kind == SimActionKind.SendMessage && action.ChannelId == GeneralId && action.MentionEveryone,
            E2EOracles.Timeout,
            due.Sequence);
        var deadline = DateTimeOffset.UtcNow + E2EOracles.Timeout;
        while (await host.ScalarAsync<long>(
            "SELECT count(*) FROM meeting_schedule WHERE clan_id = @clan AND status = 'active' AND next_run_at > now() + interval '4 minutes';",
            ("clan", ClanId)) != 1)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The second schedule was not postponed.");
            await Task.Delay(50);
        }

        // CAND-27: the second schedule's requester is told privately that it waits for a room.
        await host.Recorder.WaitForAsync(static action => action.Kind == SimActionKind.SendEphemeral && action.ChannelId == GeneralId, E2EOracles.Timeout, due.Sequence);
        await E2EOracles.AssertAsync(host, due, new ScenarioExpectation { OtherOutputs = 2 });
        Assert.Equal($"@here Mọi người tham gia phòng {VoiceLabel} để bắt đầu cuộc hội thoại.", E2EContent.Parse(suggestion).Text);
        Assert.StartsWith("Cuộc hội thoại được lên lịch: ", E2EContent.Parse(suggestion).Embeds![0].Title, StringComparison.Ordinal);
        var rows = await SchedulesAsync(host);
        Assert.Equal(new[] { "active", "completed" }, rows.Select(static row => (string)row[1]!).Order());
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM meeting_session WHERE clan_id = @clan;", ("clan", ClanId)));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM voice_claim WHERE clan_id = @clan AND voice_channel_id = @voice;", ("clan", ClanId), ("voice", VoiceId)));
    }

    private static async Task<(SimAction List, long Card)> ListAsync(MonzeE2EHost host, List<InputExpectation> inputs)
    {
        var command = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*meeting");
        inputs.Add(new(command, ResponseKind.Ephemeral));
        var list = E2EActions.NewMessageAfter(host, command, GeneralId);
        return (list, list.MessageId);
    }

    /// <summary>extra_data as the web client sends the schedule form (inputs, date picker, select).</summary>
    private static string Form(string name, string date, string time, string frequency)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [MeetingButtonId.ScheduleName] = new Dictionary<string, string> { ["value"] = name },
            [MeetingButtonId.ScheduleDate] = date,
            [MeetingButtonId.ScheduleTime] = new Dictionary<string, string> { ["value"] = time },
            [MeetingButtonId.ScheduleFrequency] = new Dictionary<string, string[]> { ["values"] = [frequency] }
        });

    private static async Task<IReadOnlyList<object?[]>> SchedulesAsync(MonzeE2EHost host)
        => await host.RowsAsync(
            "SELECT id, status, kind FROM meeting_schedule WHERE clan_id = @clan ORDER BY id;",
            ("clan", ClanId));

    private static string Answer(MonzeE2EHost host, SimPush command)
        => E2EContent.Visible(E2EActions.NewMessageAfter(host, command, GeneralId));
}
