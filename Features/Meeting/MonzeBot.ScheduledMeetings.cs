using System.Collections.Concurrent;
using Mezon.Net.Client;
using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;
using Monze.Ui;
using Microsoft.Extensions.Logging;
using SdkMezonClient = Mezon.Net.Sdk.MezonClient;

namespace Monze;

public sealed partial class MonzeBot
{
    /// <summary>meeting_schedule.last_error of an occurrence postponed because no voice room was free.</summary>
    private const string ScheduleNoVoiceRoom = "no-voice-room";

    private async Task FlushScheduledMeetingsAsync(
        SdkMezonClient client,
        CancellationToken cancellationToken)
    {
        var schedules = await _scheduling.ClaimDueMeetingSchedulesAsync(cancellationToken);
        for (var i = 0; i < schedules.Count; i++)
        {
            var schedule = schedules[i];
            try
            {
                var voice = await PickVoiceAsync(
                    client,
                    schedule.ClanId,
                    cancellationToken);
                if (voice is null)
                {
                    await _scheduling.CompleteMeetingScheduleAsync(
                        schedule.Id,
                        schedule.LeaseToken,
                        _time.GetUtcNow().AddMinutes(5),
                        true,
                        cancellationToken,
                        ScheduleNoVoiceRoom);

                    // CAND-27: the requester hears once per occurrence, not on every 5-minute retry.
                    if (schedule.LastError != ScheduleNoVoiceRoom)
                    {
                        await NotifySchedulePostponedAsync(client, schedule, cancellationToken);
                    }

                    continue;
                }

                DateTimeOffset? next = null;
                if (schedule.Kind != MeetingScheduleKind.Once)
                {
                    if (!MeetingScheduleCalculator.TryGetNext(
                            schedule.Kind,
                            schedule.WhenText,
                            schedule.TimeZoneId,
                            _time.GetUtcNow().AddSeconds(1),
                            out var nextRun,
                            out _))
                    {
                        await _scheduling.CompleteMeetingScheduleAsync(
                            schedule.Id,
                            schedule.LeaseToken,
                            null,
                            false,
                            cancellationToken);
                        continue;
                    }

                    next = nextRun;
                }

                var invitation = new MeetingInvitation(
                    voice.VoiceChannelId,
                    voice.Label,
                    schedule.Name);
                var committed = await _scheduledMeeting.CommitScheduledMeetingAsync(
                    schedule,
                    voice.VoiceChannelId,
                    _time.GetUtcNow().AddMinutes(20),
                    next,
                    MonzeMessages.MeetingAgentInstruction,
                    MonzeMessageBuilder.MeetingInvitation(
                        invitation,
                        MonzeMessages.MeetingAgentInstruction).ToJson(),
                    mentionEveryone: true,
                    cancellationToken,
                    voiceChannelLabel: voice.Label);
                if (!committed)
                {
                    await _scheduling.CompleteMeetingScheduleAsync(
                        schedule.Id,
                        schedule.LeaseToken,
                        _time.GetUtcNow().AddMinutes(1),
                        true,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Scheduled meeting {ScheduleId} failed.",
                    schedule.Id);
                await _scheduling.CompleteMeetingScheduleAsync(
                    schedule.Id,
                    schedule.LeaseToken,
                    _time.GetUtcNow().AddMinutes(1),
                    true,
                    cancellationToken);
            }
        }
    }

    /// <summary>Tells the requester, privately in the schedule's channel, that the meeting waits for a free room (with its cancel button).</summary>
    private async Task NotifySchedulePostponedAsync(
        SdkMezonClient client,
        DueMeetingSchedule schedule,
        CancellationToken cancellationToken)
    {
        try
        {
            var channel = await client.GetChannelAsync(schedule.ChannelId, cancellationToken);
            await channel.SendEphemeralAsync(
                MonzeMessageBuilder.SchedulePostponed(schedule.Name, schedule.Id),
                schedule.RequesterId);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The requester of schedule {ScheduleId} could not be told it was postponed.", schedule.Id);
        }
    }
}
