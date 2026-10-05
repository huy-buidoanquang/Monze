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
                        DateTimeOffset.UtcNow.AddMinutes(5),
                        true,
                        cancellationToken);
                    continue;
                }

                DateTimeOffset? next = null;
                if (schedule.Kind != MeetingScheduleKind.Once)
                {
                    if (!MeetingScheduleCalculator.TryGetNext(
                            schedule.Kind,
                            schedule.WhenText,
                            schedule.TimeZoneId,
                            DateTimeOffset.UtcNow.AddSeconds(1),
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
                    DateTimeOffset.UtcNow.AddMinutes(20),
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
                        DateTimeOffset.UtcNow.AddMinutes(1),
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
                    DateTimeOffset.UtcNow.AddMinutes(1),
                    true,
                    cancellationToken);
            }
        }
    }

}
