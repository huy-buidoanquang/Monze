using System.Text.Json;
using System.Threading.Channels;
using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Agent;
using Mezon.Net.Sdk.Caching.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;
using Monze.Ui;

namespace Monze;

public sealed partial class MonzeBot
{
    private Task EnqueueAgentAsync(AgentSseSessionEvent evt, AgentEventKind kind)
    {
        return EnqueueMeetingAsync(MeetingIngressItem.FromAgent(evt, kind));
    }

    private Task EnqueueMeetingContextCloseAsync(long clanId, long voiceChannelId)
        => EnqueueMeetingAsync(MeetingIngressItem.VoiceEmpty(clanId, voiceChannelId));

    private Task EnqueueMeetingContextResetAsync()
        => EnqueueMeetingAsync(MeetingIngressItem.RealtimeReset());

    private Task EnqueueMeetingAsync(MeetingIngressItem item)
    {
        if (_meetingIngress.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _agentIngressDepth);
            return Task.CompletedTask;
        }

        MonzeMetrics.AgentIngressBackpressure.Add(1);
        return WriteMeetingAsync(item);
    }
    private async Task WriteMeetingAsync(MeetingIngressItem item)
    {
        Interlocked.Increment(ref _agentPendingWriters);
        try
        {
            await _meetingIngress.Writer.WriteAsync(item);
            Interlocked.Increment(ref _agentIngressDepth);
        }
        catch (ChannelClosedException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _agentPendingWriters);
        }
    }
    /// <summary>
    /// Saves the Agent SSE resume cursor once the queue is empty, so a restart
    /// resumes after the last event processed here (DEF-08).
    /// </summary>
    private void SaveAgentCursor()
    {
        try
        {
            _agentEvents?.SaveCursor();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The Agent SSE resume cursor could not be saved.");
        }
    }

    private async Task ConsumeAgentEventsAsync(
        MezonClient client,
        CancellationToken cancellationToken)
    {
        await foreach (var item in _meetingIngress.Reader.ReadAllAsync(cancellationToken))
        {
            Interlocked.Decrement(ref _agentIngressDepth);
            try
            {
                switch (item.Kind)
                {
                    case MeetingIngressKind.AgentEvent when item.AgentEvent is not null:
                        await ProcessAgentAsync(
                            client,
                            item.AgentEvent,
                            item.AgentKind,
                            cancellationToken);
                        break;
                    case MeetingIngressKind.VoiceEmpty:
                        await _meeting.CloseMeetingContextAsync(
                            item.ClanId,
                            item.VoiceChannelId,
                            cancellationToken);
                        break;
                    case MeetingIngressKind.VoiceProfile
                        when item.ClanId > 0
                             && item.UserId > 0
                             && item.UserId != MezonAgentIdentity.UserId
                             && !string.IsNullOrWhiteSpace(item.ParticipantLabel):
                        await _userProfiles.UpsertAsync(
                            item.ClanId,
                            item.UserId,
                            null,
                            item.ParticipantLabel,
                            null,
                            null,
                            cancellationToken);
                        break;
                    case MeetingIngressKind.RealtimeReset:
                        await _meeting.CloseStartedMeetingContextsAsync(cancellationToken);
                        break;
                }

                if (item.Kind == MeetingIngressKind.AgentEvent && Volatile.Read(ref _agentIngressDepth) == 0)
                {
                    SaveAgentCursor();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent event worker failed.");
            }
        }
    }
}
