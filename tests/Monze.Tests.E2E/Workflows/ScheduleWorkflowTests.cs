using System.Globalization;
using Mezon.Net.Core;
using Monze.Application.Commands;
using Monze.Domain;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Workflows;

/// <summary>
/// Scheduled meetings firing for real (Features/Meeting/MonzeBot.ScheduledMeetings.cs,
/// PostgresSchedulingRepository): due times are warped on the campaign
/// database only (next_run_at), the scheduler runs every 200 ms. A due once,
/// daily and weekly schedule each post one invitation in a free room; the
/// once schedule completes and the recurring ones move to their next
/// occurrence; a schedule in the same slot without a free room waits 5
/// minutes; a schedule cancelled while the worker holds its lease stays
/// cancelled and fires nothing; a schedule whose lease went stale is
/// reclaimed and fires once.
/// </summary>
public sealed class ScheduleWorkflowTests
{
    private const long SecondVoiceId = 1_840_000_000_000_006_104L;
    private const long ThirdVoiceId = 1_840_000_000_000_006_105L;
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    [DbFact]
    [Req("REQ-MTG-002")]
    public async Task Due_once_daily_and_weekly_schedules_fire_one_occurrence_each_and_a_slot_conflict_waits()
    {
        var world = AreaWorld.Create();
        world.AddChannel(ClanId, SecondVoiceId, "Phòng hai", (int)ChannelType.MezonVoice);
        world.AddChannel(ClanId, ThirdVoiceId, "Phòng ba", (int)ChannelType.MezonVoice);
        await using var host = await E2EActions.StartAsync("wf_schedule_fire", world);
        var (date, time) = Slot();
        var setup = await E2EOracles.MarkAsync(host);
        var inputs = new List<InputExpectation>();
        foreach (var (title, kind) in new[] { ("Once", "once"), ("Daily", "daily"), ("Weekly", "weekly"), ("Clash", "once") })
        {
            inputs.Add(new(await E2EActions.CommandAsync(host, GeneralId, OwnerId, $"*meeting {title} {date} {time} {kind}"), ResponseKind.Reply));
        }

        await E2EOracles.AssertAsync(host, setup, new ScenarioExpectation { Inputs = inputs });
        var due = await E2EOracles.MarkAsync(host);
        await WarpDueAsync(host, "TRUE");
        await host.Recorder.WaitForCountAsync(static action => action.Kind == SimActionKind.SendMessage && action.MentionEveryone, 3, E2EOracles.Timeout, due.Sequence);
        await E2EActions.WaitUntilAsync(
            host,
            async () => await host.ScalarAsync<long>("SELECT count(*) FROM meeting_schedule WHERE status = 'running';") == 0,
            "the scheduler to release its leases");
        await E2EOracles.AssertAsync(host, due, new ScenarioExpectation { OtherOutputs = 3 });

        var rows = (await host.RowsAsync("SELECT title, kind, status, next_run_at, when_text, timezone FROM meeting_schedule ORDER BY id;")).ToDictionary(static row => (string)row[0]!);
        Assert.Equal("completed", rows["Once"][2]);
        foreach (var (title, kind) in new[] { ("Daily", MeetingScheduleKind.Daily), ("Weekly", MeetingScheduleKind.Weekly) })
        {
            Assert.Equal("active", rows[title][2]);
            Assert.True(MeetingScheduleCalculator.TryGetNext(kind, (string)rows[title][4]!, (string)rows[title][5]!, DateTimeOffset.UtcNow.AddSeconds(1), out var next, out _));
            Assert.Equal(next.UtcDateTime, ((DateTime)rows[title][3]!).ToUniversalTime());
        }

        Assert.Equal("active", rows["Clash"][2]);
        var postponed = ((DateTime)rows["Clash"][3]!).ToUniversalTime() - DateTime.UtcNow;
        Assert.InRange(postponed, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5.1));
        var sessions = await host.RowsAsync("SELECT voice_channel_id, meeting_title FROM meeting_session WHERE clan_id = @clan AND status = 'suggested' ORDER BY id;", ("clan", ClanId));
        Assert.Equal(new[] { "Once", "Daily", "Weekly" }, sessions.Select(static row => (string)row[1]!));
        Assert.Equal(3, sessions.Select(static row => (long)row[0]!).Distinct().Count());
        await E2EOracles.AssertInvariantsAsync(host);
    }

    [DbFact]
    [Req("REQ-MTG-002")]
    [Covers("msg:MeetingScheduleCancelled")]
    public async Task A_busy_room_postpones_a_cancel_during_the_lease_wins_and_a_stale_lease_is_reclaimed()
    {
        await using var host = await E2EActions.StartAsync("wf_schedule_faults");
        var (date, time) = Slot();
        var setup = await E2EOracles.MarkAsync(host);
        var busy = await E2EActions.CommandAsync(host, GeneralId, OwnerId, $"*meeting Busy {date} {time} once");
        var cancelled = await E2EActions.CommandAsync(host, GeneralId, OwnerId, $"*meeting Cancelled {date} {time} once");
        var stale = await E2EActions.CommandAsync(host, GeneralId, OwnerId, $"*meeting Stale {date} {time} once");
        await E2EOracles.AssertAsync(host, setup, ScenarioExpectation.Of((busy, ResponseKind.Reply), (cancelled, ResponseKind.Reply), (stale, ResponseKind.Reply)));
        var ids = (await host.RowsAsync("SELECT title, id FROM meeting_schedule;")).ToDictionary(static row => (string)row[0]!, static row => (long)row[1]!);

        // The only room is occupied: the due schedule waits 5 minutes.
        var phase = await E2EOracles.MarkAsync(host);
        await host.Inbound.VoiceJoinAsync(ClanId, VoiceId, Member2Id);
        await WarpDueAsync(host, "title = 'Busy'");
        await E2EActions.WaitUntilAsync(
            host,
            async () => await host.ScalarAsync<long>("SELECT count(*) FROM meeting_schedule WHERE title = 'Busy' AND status = 'active' AND next_run_at > now() + interval '4 minutes';") == 1,
            "the busy schedule to be postponed");

        // The room frees up; the next schedule is cancelled while the worker holds its lease.
        await host.Inbound.VoiceLeaveAsync(ClanId, VoiceId, Member2Id);
        host.Simulator.Faults.Delay(SimOperations.ListChannelDescs, TimeSpan.FromSeconds(2));
        await WarpDueAsync(host, "title = 'Cancelled'");
        await E2EActions.WaitUntilAsync(
            host,
            async () => await host.ScalarAsync<string>("SELECT status FROM meeting_schedule WHERE title = 'Cancelled';") == "running",
            "the worker to lease the schedule");
        var cancel = await E2EActions.CommandAsync(host, GeneralId, OwnerId, $"*meeting cancel {ids["Cancelled"]}");
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Equal("cancelled", await host.ScalarAsync<string>("SELECT status FROM meeting_schedule WHERE title = 'Cancelled';"));
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM meeting_session WHERE meeting_title = 'Cancelled';"));
        await E2EOracles.AssertAsync(host, phase, ScenarioExpectation.Of((cancel, ResponseKind.Reply)));
        Assert.Contains(MonzeMessages.MeetingScheduleCancelled, E2EContent.Visible(E2EActions.NewMessageAfter(host, cancel, GeneralId)));

        // A worker died holding the lease of the next schedule.
        var reclaim = await E2EOracles.MarkAsync(host);
        await host.ScalarAsync<int>(
            "UPDATE meeting_schedule SET status = 'running', lease_token = 'stale-lease', locked_until = now() - interval '2 seconds', next_run_at = now() - interval '1 second' WHERE title = 'Stale' RETURNING 1;");
        var invitation = await host.Recorder.WaitForAsync(static action => action.Kind == SimActionKind.SendMessage && action.MentionEveryone, E2EOracles.Timeout, reclaim.Sequence);
        await E2EActions.WaitUntilAsync(host, async () => await host.ScalarAsync<string>("SELECT status FROM meeting_schedule WHERE title = 'Stale';") == "completed", "the stale schedule to complete");
        await E2EOracles.AssertAsync(host, reclaim, new ScenarioExpectation { OtherOutputs = 1 });
        Assert.StartsWith("Cuộc hội thoại được lên lịch: Stale", E2EContent.Parse(invitation).Embeds![0].Title, StringComparison.Ordinal);
        Assert.Equal(new object?[] { "Stale", "suggested" }, Assert.Single(await host.RowsAsync("SELECT meeting_title, status FROM meeting_session WHERE clan_id = @clan;", ("clan", ClanId))));
        await E2EOracles.AssertInvariantsAsync(host);
    }

    /// <summary>A date and time three hours ahead in Vietnam time, so no occurrence is due before the warp.</summary>
    private static (string Date, string Time) Slot()
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddHours(3), Vietnam);
        return (local.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), local.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    /// <summary>Time warp on the campaign database: the matching active schedules are due now.</summary>
    private static Task<int> WarpDueAsync(MonzeE2EHost host, string where)
        => host.ScalarAsync<int>($"UPDATE meeting_schedule SET next_run_at = now() - interval '1 second' WHERE status = 'active' AND {where} RETURNING 1;");
}
