using Mezon.Net.Sdk.Agent;
using Mezon.Net.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Monze.Application;
using Monze.Ui;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task ProcessAgentAsync(
        MezonClient client,
        AgentSseSessionEvent evt,
        AgentEventKind kind,
        CancellationToken cancellationToken)
    {
        var eventKey = AgentEventIdentity.Compute((byte)kind, evt.EventType, evt.RawResponse);
        if (!await _meeting.TryClaimInboxAsync("agent", eventKey, cancellationToken))
        {
            return;
        }

        try
        {
            if (!AgentEventPayload.TryParse(evt.RawResponse, out var payload)
                || string.IsNullOrWhiteSpace(payload.RoomId))
            {
                return;
            }

            var roomId = payload.RoomId;

            if (kind == AgentEventKind.Ended)
            {
                var ended = await _meeting.MarkMeetingEndedAsync(roomId, cancellationToken);
                if (ended is not null)
                {
                    await UpdateAgentStatusAsync(client, ended, summarizing: true, cancellationToken);
                }
                return;
            }

            if (kind == AgentEventKind.SummaryDone)
            {
                var summary = await FetchSummaryWithRetryAsync(roomId, cancellationToken);
                if (string.IsNullOrWhiteSpace(summary))
                {
                    await _meeting.MarkSummaryPendingAsync(roomId, cancellationToken);
                    await _meeting.ReleaseInboxAsync("agent", eventKey, cancellationToken);
                }
                else
                {
                    await _meeting.StoreSummaryAsync(roomId, summary, null, cancellationToken);
                }

                return;
            }

            if (payload.VoiceChannelId is not long voiceId)
            {
                return;
            }

            if (payload.ClanId is long clanId)
            {
                var binding = await _meeting.BindAgentSessionAsync(
                    clanId,
                    voiceId,
                    roomId,
                    _configuration.GetValue<long>("Mezon:BotId"),
                    cancellationToken);
                if (binding is not null)
                {
                    await UpdateAgentStatusAsync(client, binding, summarizing: false, cancellationToken);
                }
            }
            else
            {
                await _meeting.BindMeetingRoomByVoiceChannelAsync(voiceId, roomId, cancellationToken);
            }
        }
        catch
        {
            try
            {
                await _meeting.ReleaseInboxAsync("agent", eventKey, cancellationToken);
            }
            catch (Exception releaseException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(releaseException, "Could not release Agent inbox claim {EventKey} after processing failure.", eventKey);
            }

            throw;
        }
    }

    private async Task UpdateAgentStatusAsync(
        MezonClient client,
        MeetingSessionBinding binding,
        bool summarizing,
        CancellationToken cancellationToken)
    {
        // Meeting sessions created by Monze always notify the text channel
        // that started the meeting. A direct Agent session has the voice ID
        // copied into TextChannelId because no source text channel exists.
        var channelId = binding.TextChannelId;
        var content = summarizing
            ? MonzeMessageBuilder.AgentSummarizing()
            : MonzeMessageBuilder.AgentWaiting();
        if (binding.NotificationMessageId is long messageId
            && messageId > 0
            && binding.NotificationChannelId == channelId)
        {
            var channel = await client.GetChannelAsync(channelId, cancellationToken);
            await channel.UpdateMessageAsync(messageId, content);
            return;
        }

        var target = await client.GetChannelAsync(channelId, cancellationToken);
        var ack = await target.SendAsync(content);
        await _meeting.SetSessionNotificationMessageAsync(
            binding.SessionId,
            channelId,
            ack.MessageId,
            cancellationToken);
    }

    private async Task<string?> FetchSummaryWithRetryAsync(
        string roomId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var summary = await _transcript.FetchSummaryAsync(roomId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                return summary;
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken);
            }
        }

        return null;
    }
}

