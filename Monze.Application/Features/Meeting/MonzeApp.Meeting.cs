using Monze.Application.Commands;
using Monze.Domain;

namespace Monze.Application;

public sealed partial class MonzeApp
{
    /// <summary>A new schedule this close to another one the creator can see is reported (CAND-27).</summary>
    private static readonly TimeSpan ScheduleOverlapWindow = TimeSpan.FromMinutes(30);

    public async Task<CommandOutcome> HandleMeetingAsync(
        long clanId,
        long channelId,
        long userId,
        IReadOnlyList<string> args,
        Func<CancellationToken, Task<MeetingVoiceCandidate?>> pickVoice,
        CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            var schedules = await _scheduling.ListMeetingSchedulesAsync(
                clanId,
                channelId,
                userId,
                20,
                cancellationToken);
            return new CommandOutcome
            {
                Title = MonzeMessages.TitleMeeting,
                Text = schedules.Count == 0 ? MonzeMessages.MeetingScheduleEmpty : string.Empty,
                ShowMeetingSchedules = true,
                MeetingSchedules = schedules
            };
        }

        if (!MeetingCommandParser.TryParse(args, out var request) || request is null)
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Meeting, cancellationToken);
        }

        if (request.IsCancel)
        {
            var cancelled = await _scheduling.CancelMeetingScheduleAsync(
                clanId,
                channelId,
                userId,
                request.CancelScheduleId!.Value,
                cancellationToken);
            return Say(cancelled ? MonzeMessages.MeetingScheduleCancelled : MonzeMessages.MeetingScheduleNotFound,
                title: MonzeMessages.TitleMeeting,
                tone: cancelled ? MonzeTone.Ok : MonzeTone.Warn);
        }

        if (request.Kind != MeetingScheduleKind.Now)
        {
            const string timeZoneId = "Asia/Ho_Chi_Minh";
            if (!MeetingScheduleCalculator.TryGetNext(
                    request.Kind,
                    request.WhenText!,
                    timeZoneId,
                    _time.GetUtcNow(),
                    out var next,
                    out var error))
            {
                return Say(error ?? MonzeMessages.InvalidTime);
            }

            // CAND-27: warn about the creator's other schedules here (all of them for an admin) that
            // run less than 30 minutes from this one; the list is read before this one is saved.
            var nearby = (await _scheduling.ListMeetingSchedulesAsync(clanId, channelId, userId, 20, cancellationToken))
                .Where(schedule => (schedule.NextRunAt - next).Duration() < ScheduleOverlapWindow)
                .ToList();
            var scheduleId = await _scheduling.CreateMeetingScheduleAsync(
                clanId,
                channelId,
                userId,
                request.Name ?? "Cuộc họp",
                request.Kind,
                request.WhenText!,
                timeZoneId,
                next,
                cancellationToken);
            var saved = MonzeMessages.ScheduleSaved(
                request.Name ?? "Cuộc họp",
                scheduleId,
                request.Kind,
                next,
                timeZoneId);
            return nearby.Count == 0
                ? Say(saved, title: MonzeMessages.TitleMeeting, tone: MonzeTone.Ok)
                : Say(saved + "\n" + MonzeMessages.ScheduleOverlap(nearby), title: MonzeMessages.TitleMeeting, tone: MonzeTone.Warn);
        }

        var voice = await pickVoice(cancellationToken);
        if (voice is null)
        {
            return Say(MonzeMessages.NoVoiceRoom);
        }

        var sessionId = await _meeting.CreateMeetingAsync(clanId, channelId, userId, null, cancellationToken);
        if (!await _meeting.SuggestMeetingAsync(
                sessionId,
                voice.VoiceChannelId,
                _time.GetUtcNow().AddMinutes(20),
                cancellationToken,
                voice.Label))
        {
            return Say(MonzeMessages.VoiceClaimConflict, tone: MonzeTone.Warn);
        }

        return new CommandOutcome
        {
            Title = MonzeMessages.TitleMeeting,
            Text = MonzeMessages.MeetingAgentInstruction,
            Tone = MonzeTone.Ok,
            MeetingInvitation = new MeetingInvitation(voice.VoiceChannelId, voice.Label, SessionId: sessionId)
        };
    }

    public async Task<CommandOutcome> HandleSummaryAsync(
        long clanId,
        long userId,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (clanId <= 0)
        {
            return Say(MonzeMessages.UnknownClan, title: MonzeMessages.TitleSummary, tone: MonzeTone.Error);
        }

        if (args.Count > 0 && args[0].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase))
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Summary, cancellationToken);
        }

        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.SummaryAdminOnly, title: MonzeMessages.TitleSummary, tone: MonzeTone.Error);
        }

        if (args.Count != 1
            || !long.TryParse(args[0], out var sessionId)
            || sessionId <= 0)
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Summary, cancellationToken);
        }

        var summary = await _meeting.GetSummaryAsync(clanId, sessionId, cancellationToken);
        if (summary is null)
        {
            return Say(MonzeMessages.NoSummary, title: MonzeMessages.TitleSummary, tone: MonzeTone.Warn);
        }

        return new CommandOutcome
        {
            Title = MonzeMessages.TitleSummary,
            Text = string.Empty,
            MeetingSummary = summary
        };
    }
}
