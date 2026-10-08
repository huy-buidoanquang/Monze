using Mezon.Net.Sdk.Agent;
using Mezon.Net.Sdk;
using Mezon.Net.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Monze.Application;
using Monze.Domain;
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
        if (!AgentEventPayload.TryParse(evt.RawResponse, out var payload)
            || string.IsNullOrWhiteSpace(payload.RoomId))
        {
            MonzeMetrics.AgentEventsIgnored.Add(1);
            return;
        }

        var scope = await ResolveAgentScopeAsync(
            client,
            payload,
            kind,
            cancellationToken);
        if (scope is null)
        {
            MonzeMetrics.AgentEventsIgnored.Add(1);
            return;
        }

        var eventKey = AgentEventIdentity.Compute((byte)kind, evt.EventType, evt.RawResponse);
        if (!await _meeting.TryClaimInboxAsync("agent", eventKey, cancellationToken))
        {
            return;
        }

        _logger.LogInformation(
            "Agent event accepted. Kind={Kind}, EventType={EventType}, PayloadLength={PayloadLength}.",
            kind,
            evt.EventType,
            evt.RawResponse.Length);

        try
        {
            var roomId = payload.RoomId!;

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
                var summary = await _transcript.FetchSummaryAsync(roomId, cancellationToken);
                _logger.LogInformation(
                    "Agent summary completion processed. HasSummary={HasSummary}.",
                    summary is not null);
                if (summary is null || string.IsNullOrWhiteSpace(summary.Summary))
                {
                    await _meeting.MarkSummaryPendingAsync(roomId, cancellationToken);
                    await _meeting.ReleaseInboxAsync("agent", eventKey, cancellationToken);
                }
                else
                {
                    var summaryContext = await _meeting.GetSummaryContextAsync(roomId, cancellationToken);
                    var delivery = summaryContext is null
                        ? null
                        : await _summaryComposer.ComposeAsync(summary, summaryContext, cancellationToken);
                    var stored = await _meeting.StoreSummaryAsync(
                        roomId,
                        summary.Summary,
                        summary.FullTranscriptJson,
                        delivery,
                        cancellationToken);
                    if (!stored)
                    {
                        _logger.LogWarning(
                            "Agent summary could not be matched to a meeting session. RoomId={RoomId}.",
                            roomId);
                        await _meeting.MarkSummaryPendingAsync(roomId, cancellationToken);
                        await _meeting.ReleaseInboxAsync("agent", eventKey, cancellationToken);
                    }
                }

                return;
            }

            var agentBinding = await _meeting.BindAgentSessionAsync(
                scope.Value.ClanId,
                scope.Value.VoiceChannelId,
                roomId,
                _configuration.GetValue<long>("Mezon:BotId"),
                cancellationToken,
                scope.Value.VoiceChannelLabel);
            if (agentBinding is { Status: MeetingStatus.Live or MeetingStatus.SummaryPending })
            {
                await UpdateAgentStatusAsync(
                    client,
                    agentBinding,
                    summarizing: agentBinding.Status == MeetingStatus.SummaryPending,
                    cancellationToken);
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

    private async ValueTask<(long ClanId, long VoiceChannelId, string? VoiceChannelLabel)?>
        ResolveAgentScopeAsync(
            MezonClient client,
            AgentEventPayload payload,
            AgentEventKind kind,
            CancellationToken cancellationToken)
    {
        if (payload.ClanId is long payloadClanId && !IsKnownClan(payloadClanId))
        {
            return null;
        }

        if (payload.VoiceChannelId is not long voiceChannelId)
        {
            return kind == AgentEventKind.Started
                ? null
                : await ResolveBoundAgentScopeAsync(payload, cancellationToken);
        }

        if (_voiceClanByChannel.TryGetValue(voiceChannelId, out var trackedClanId))
        {
            return TryCreateAgentScope(
                payload,
                voiceChannelId,
                trackedClanId,
                (int)ChannelType.MezonVoice,
                client.Channels.TryGet(voiceChannelId, out var trackedChannel)
                    ? trackedChannel.Name
                    : null);
        }

        if (client.Channels.TryGet(voiceChannelId, out var cachedChannel))
        {
            return TryCreateAgentScope(
                payload,
                voiceChannelId,
                cachedChannel.ClanId,
                cachedChannel.Type,
                cachedChannel.Name);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var channel = await client.GetChannelDetailAsync(
                    voiceChannelId,
                    new RequestOptions { SocketSendTimeout = 5_000 });
                return TryCreateAgentScope(
                    payload,
                    voiceChannelId,
                    channel.ClanId,
                    channel.Type,
                    channel.ChannelLabel);
            }
            catch (MezonApiException ex) when (IsTerminalAgentChannelLookup(ex.StatusCode))
            {
                return kind == AgentEventKind.Started
                    ? null
                    : await ResolveBoundAgentScopeAsync(payload, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == 2)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not resolve an Agent voice channel after three transient attempts.");
                    return kind == AgentEventKind.Started
                        ? null
                        : await ResolveBoundAgentScopeAsync(payload, cancellationToken);
                }

                await Task.Delay(
                    _timings.AgentScopeRetryBase * (attempt + 1),
                    _time,
                    cancellationToken);
            }
        }

        return null;
    }

    private (long ClanId, long VoiceChannelId, string? VoiceChannelLabel)? TryCreateAgentScope(
        AgentEventPayload payload,
        long voiceChannelId,
        long clanId,
        int channelType,
        string? voiceChannelLabel)
    {
        if (!AgentEventScopePolicy.Allows(
                payload.ClanId,
                clanId,
                channelType,
                Volatile.Read(ref _knownClanIds)))
        {
            return null;
        }

        TrackVoiceChannel(clanId, voiceChannelId);
        return (clanId, voiceChannelId, voiceChannelLabel);
    }

    private async ValueTask<(long ClanId, long VoiceChannelId, string? VoiceChannelLabel)?>
        ResolveBoundAgentScopeAsync(
            AgentEventPayload payload,
            CancellationToken cancellationToken)
    {
        var context = await _meeting.GetSummaryContextAsync(
            payload.RoomId!,
            cancellationToken);
        if (context is null
            || !IsKnownClan(context.ClanId)
            || payload.VoiceChannelId is long payloadVoiceId
                && payloadVoiceId != context.VoiceChannelId
            || payload.ClanId is long payloadClanId
                && payloadClanId != context.ClanId)
        {
            return null;
        }

        TrackVoiceChannel(context.ClanId, context.VoiceChannelId);
        return (context.ClanId, context.VoiceChannelId, context.VoiceChannelLabel);
    }

    internal static bool IsTerminalAgentChannelLookup(MezonStatusCode statusCode)
        => statusCode is MezonStatusCode.InvalidArgument
            or MezonStatusCode.NotFound
            or MezonStatusCode.PermissionDenied
            or MezonStatusCode.FailedPrecondition
            or MezonStatusCode.Unimplemented;

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
        var content = binding.DirectAgent
            ? summarizing
                ? MonzeMessageBuilder.AgentSummarizing()
                : MonzeMessageBuilder.AgentWaiting()
            : MonzeMessageBuilder.MeetingAgentStatus(binding, summarizing);
        var statusMessageId = binding.SourceMessageId ?? binding.NotificationMessageId;
        if (statusMessageId is long messageId
            && messageId > 0
            && binding.NotificationChannelId == channelId)
        {
            var channel = await client.GetChannelAsync(channelId, cancellationToken);
            await channel.UpdateMessageAsync(
                messageId,
                content,
                mentions: binding.DirectAgent ? null : MonzeMentionMetadata.Here);
            return;
        }

        var target = await client.GetChannelAsync(channelId, cancellationToken);
        var ack = await target.SendAsync(
            content,
            mentionEveryone: !binding.DirectAgent,
            mentions: binding.DirectAgent ? null : MonzeMentionMetadata.Here);
        var ackMessageId = TryReadMessageId(ack);
        if (ackMessageId <= 0)
        {
            _logger.LogWarning(
                "Agent status message returned an empty ACK. SessionId={SessionId}, ChannelId={ChannelId}.",
                binding.SessionId,
                channelId);
            return;
        }

        await _meeting.SetSessionStatusMessageAsync(
            binding.SessionId,
            channelId,
            ackMessageId,
            cancellationToken);
    }

}

