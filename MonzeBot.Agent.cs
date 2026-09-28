using Mezon.Net.Sdk.Agent;
using Microsoft.Extensions.Logging;
using Monze.Application;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task ProcessAgentAsync(
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
                await _meeting.MarkMeetingEndedAsync(roomId, cancellationToken);
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
                await _meeting.BindMeetingRoomAsync(clanId, voiceId, roomId, cancellationToken);
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

