using System.Collections.Concurrent;
using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;
using Monze.Ui;
using Microsoft.Extensions.Logging;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task HandleMeetingAsync(ICommandContext context)
        => await ExecuteCommandOnceAsync(context, () => HandleMeetingCoreAsync(context));

    private async Task HandleMeetingCoreAsync(
        ICommandContext context,
        IReadOnlyList<string>? commandArguments = null)
    {
        var args = commandArguments ?? context.Args;
        var clanId = context.Clan?.Id ?? 0;
        if (clanId == 0)
        {
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleMeeting,
                MonzeMessages.UnknownClan,
                MonzeTone.Error));
            return;
        }

        if (!_commandRateLimiter.TryAcquire(
                clanId,
                context.Author.Id,
                MonzeCommandNames.Meeting,
                DateTimeOffset.UtcNow,
                out var retryAfter))
        {
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleRateLimited,
                MonzeMessages.RateLimited(retryAfter),
                MonzeTone.Warn));
            return;
        }

        try
        {
            if (args.Count > 0
                && args[0].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase))
            {
                var help = await _app.HandleMonzeAsync(
                    clanId,
                    context.Channel.Id,
                    context.Author.Id,
                    new CommandArguments(new[] { MonzeCommandNames.Help, MonzeCommandNames.Meeting }),
                    context.CancellationToken);
                await context.ReplyAsync(MonzeMessageBuilder.Card(help, _commandOptions));
                return;
            }

            var outcome = await _app.HandleMeetingAsync(
                clanId,
                context.Channel.Id,
                context.Author.Id,
                args,
                ct => PickVoiceAsync(context, clanId, ct),
                context.CancellationToken);
            long? invitationMessageId = null;
            if (outcome.MeetingInvitation is { } invitation)
            {
                var ack = await context.Channel.SendAsync(
                    MonzeMessageBuilder.MeetingInvitation(invitation),
                    mentionEveryone: true,
                    mentions: MonzeMentionMetadata.Here);
                invitationMessageId = ack.MessageId;
                _logger.LogInformation(
                    "Meeting invitation delivered to text channel {TextChannelId} for voice channel {VoiceChannelId} ({VoiceChannelLabel}); MessageId={MessageId}.",
                    context.Channel.Id,
                    invitation.VoiceChannelId,
                    invitation.VoiceChannelLabel,
                    ack.MessageId);
            }
            var response = await context.ReplyAsync(MonzeMessageBuilder.Card(outcome, _commandOptions));
            if (outcome.MeetingInvitation?.SessionId is long sessionId)
            {
                await _meeting.SetSessionNotificationMessageAsync(
                    sessionId,
                    context.Channel.Id,
                    invitationMessageId ?? response.MessageId,
                    context.CancellationToken);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Meeting command failed for clan {ClanId} and channel {ChannelId}.", clanId, context.Channel.Id);
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleMeeting,
                MonzeMessages.TemporaryFailure,
                MonzeTone.Error));
        }
    }

    private async Task HandleSummaryAsync(ICommandContext context)
        => await ExecuteCommandOnceAsync(context, () => HandleSummaryCoreAsync(context));

    private async Task HandleSummaryCoreAsync(
        ICommandContext context,
        IReadOnlyList<string>? commandArguments = null)
    {
        var args = commandArguments ?? context.Args;
        var clanId = context.Clan?.Id ?? 0;
        if (clanId == 0)
        {
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleSummary,
                MonzeMessages.UnknownClan,
                MonzeTone.Error));
            return;
        }

        if (!_commandRateLimiter.TryAcquire(
                clanId,
                context.Author.Id,
                MonzeCommandNames.Summary,
                DateTimeOffset.UtcNow,
                out var retryAfter))
        {
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleRateLimited,
                MonzeMessages.RateLimited(retryAfter),
                MonzeTone.Warn));
            return;
        }

        try
        {
            if (args.Count > 0
                && args[0].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase))
            {
                var help = await _app.HandleMonzeAsync(
                    clanId,
                    context.Channel.Id,
                    context.Author.Id,
                    new CommandArguments(new[] { MonzeCommandNames.Help, MonzeCommandNames.Summary }),
                    context.CancellationToken);
                await context.ReplyAsync(MonzeMessageBuilder.Card(help, _commandOptions));
                return;
            }

            var outcome = await _app.HandleSummaryAsync(
                clanId,
                context.Channel.Id,
                context.Author.Id,
                args,
                context.CancellationToken);
            await context.ReplyAsync(MonzeMessageBuilder.Card(outcome, _commandOptions));
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Summary command failed for channel {ChannelId}.", context.Channel.Id);
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleSummary,
                MonzeMessages.TemporaryFailure,
                MonzeTone.Error));
        }
    }

    private async Task<MeetingVoiceCandidate?> PickVoiceAsync(
        ICommandContext context,
        long clanId,
        CancellationToken cancellationToken)
        => await PickVoiceAsync(context.Client, clanId, cancellationToken);

    private async Task<MeetingVoiceCandidate?> PickVoiceAsync(
        MezonClient client,
        long clanId,
        CancellationToken cancellationToken)
    {
        var selectionGate = _voiceSelectionGates.GetOrAdd(
            clanId,
            static _ => new SemaphoreSlim(1, 1));
        await selectionGate.WaitAsync(cancellationToken);
        try
        {
            return await PickVoiceCoreAsync(client, clanId, cancellationToken);
        }
        finally
        {
            selectionGate.Release();
        }
    }

    private async Task<MeetingVoiceCandidate?> PickVoiceCoreAsync(
        MezonClient client,
        long clanId,
        CancellationToken cancellationToken)
    {
        if (!client.Clans.TryGet(clanId, out var clan))
        {
            return null;
        }

        var channels = await client.ListChannelDescsAsync(
            new ListChannelDescsParams(
                clanId: clanId,
                limit: 100,
                // The backend accepts Channel/Group/DM as list modes. Voice
                // channels are returned in the clan Channel snapshot and are
                // filtered locally by their actual type below.
                channelType: (int)ChannelType.Channel,
                page: 0),
            new RequestOptions { SocketSendTimeout = 5_000 });
        if (!await RefreshVoiceOccupancyAsync(clan, clanId, cancellationToken))
        {
            return null;
        }

        var activeClaims = await _meeting.ActiveVoiceClaimsAsync(clanId, cancellationToken);

        var listedVoiceChannels = 0;
        for (var i = 0; i < channels.Channeldesc.Count; i++)
        {
            if (channels.Channeldesc[i].Type != (int)ChannelType.MezonVoice)
            {
                continue;
            }

            listedVoiceChannels++;
            var channelId = channels.Channeldesc[i].ChannelId;
            TrackVoiceChannel(clanId, channelId);
            if (IsEmptyVoice(clanId, channelId) && !activeClaims.Contains(channelId))
            {
                return await CreateVoiceCandidateAsync(
                    client,
                    channelId,
                    channels.Channeldesc[i].ChannelLabel,
                    cancellationToken);
            }
        }

        // ListChannelDescs is cached upstream and may lag a recent public
        // channel creation. The SDK receives channel events on the clan
        // stream, so use those L1 candidates as a bounded fallback.
        var eventVoiceChannels = 0;
        if (_voiceChannelsByClan.TryGetValue(clanId, out var eventVoiceChannelIds))
        {
            foreach (var pair in eventVoiceChannelIds)
            {
                eventVoiceChannels++;
                if (IsEmptyVoice(clanId, pair.Key) && !activeClaims.Contains(pair.Key))
                {
                    return await CreateVoiceCandidateAsync(
                        client,
                        pair.Key,
                        null,
                        cancellationToken);
                }
            }
        }

        _logger.LogInformation(
            "Meeting voice discovery for clan {ClanId}: listed={ListedChannels}, listedVoice={ListedVoiceChannels}, eventVoice={EventVoiceChannels}.",
            clanId,
            channels.Channeldesc.Count,
            listedVoiceChannels,
            eventVoiceChannels);

        return null;
    }

    private static async Task<MeetingVoiceCandidate> CreateVoiceCandidateAsync(
        MezonClient client,
        long channelId,
        string? label,
        CancellationToken cancellationToken)
    {
        if (IsUsableChannelLabel(label))
        {
            return new MeetingVoiceCandidate(channelId, label!);
        }

        try
        {
            var channel = await client.GetChannelAsync(channelId, cancellationToken);
            return new MeetingVoiceCandidate(
                channelId,
                IsUsableChannelLabel(channel.Name) ? channel.Name! : "phòng voice");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new MeetingVoiceCandidate(channelId, "phòng voice");
        }
    }

    private static bool IsUsableChannelLabel(string? label)
        => !string.IsNullOrWhiteSpace(label) && !long.TryParse(label, out _);

    private async Task<bool> RefreshVoiceOccupancyAsync(
        Mezon.Net.Sdk.Entities.Clan clan,
        long clanId,
        CancellationToken cancellationToken)
    {
        _voiceSnapshotReady.TryRemove(clanId, out _);
        try
        {
            if (_voiceChannelsByClan.TryGetValue(clanId, out var knownVoiceChannels))
            {
                foreach (var pair in knownVoiceChannels)
                {
                    _voiceOccupancy.TryRemove(new VoiceKey(clanId, pair.Key), out _);
                }
            }

            var snapshot = await clan.ListChannelVoiceUsersAsync(
                0,
                (int)ChannelType.MezonVoice,
                new RequestOptions { SocketSendTimeout = 5_000 });
            for (var i = 0; i < snapshot.VoiceChannelUsers.Count; i++)
            {
                var voice = snapshot.VoiceChannelUsers[i];
                var key = new VoiceKey(clanId, voice.ChannelId);
                TrackVoiceChannel(key.ClanId, key.ChannelId);
                _voiceOccupancy[key] = voice.UserIds.Count;
            }

            _voiceSnapshotReady[clanId] = 0;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not refresh voice occupancy for clan {ClanId}.", clanId);
            return false;
        }
    }

    private bool IsEmptyVoice(long clanId, long channelId)
        => _voiceSnapshotReady.ContainsKey(clanId)
            && (!_voiceOccupancy.TryGetValue(new VoiceKey(clanId, channelId), out var count) || count == 0);

    private async Task FlushScheduledMeetingsAsync(
        MezonClient client,
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
                    $"Đã gửi lời mời cuộc họp \"{schedule.Name}\" vào phòng {voice.Label}.",
                    MonzeMessageBuilder.MeetingInvitation(invitation).ToJson(),
                    mentionEveryone: true,
                    cancellationToken);
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

    private Task OnVoiceJoinedAsync(VoiceJoinedEventEventData evt)
    {
        var voice = (VoiceJoinedEventResponse)evt;
        AdjustOccupancy(new VoiceKey(voice.ClanId, voice.VoiceChannelId), 1);
        return Task.CompletedTask;
    }

    private Task OnChannelCreatedAsync(ChannelCreatedEventEventData evt)
    {
        var channel = (ChannelCreatedEventResponse)evt;
        if (channel.ChannelType == (int)ChannelType.MezonVoice)
        {
            TrackVoiceChannel(channel.ClanId, channel.ChannelId);
        }

        return Task.CompletedTask;
    }

    private Task OnChannelUpdatedAsync(ChannelUpdatedEventEventData evt)
    {
        var channel = (ChannelUpdatedEventResponse)evt;
        var key = new VoiceKey(channel.ClanId, channel.ChannelId);
        if (channel.Status == 3 || channel.ChannelType != (int)ChannelType.MezonVoice)
        {
            UntrackVoiceChannel(key);
            _voiceOccupancy.TryRemove(key, out _);
        }
        else
        {
            TrackVoiceChannel(key.ClanId, key.ChannelId);
        }

        return Task.CompletedTask;
    }

    private Task OnChannelDeletedAsync(ChannelDeletedEventEventData evt)
    {
        var channel = (ChannelDeletedEventResponse)evt;
        var key = new VoiceKey(channel.ClanId, channel.ChannelId);
        UntrackVoiceChannel(key);
        _voiceOccupancy.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private void TrackVoiceChannel(long clanId, long channelId)
    {
        var channels = _voiceChannelsByClan.GetOrAdd(
            clanId,
            static _ => new ConcurrentDictionary<long, byte>());
        channels.TryAdd(channelId, 0);
    }

    private void UntrackVoiceChannel(VoiceKey key)
    {
        if (!_voiceChannelsByClan.TryGetValue(key.ClanId, out var channels))
        {
            return;
        }

        channels.TryRemove(key.ChannelId, out _);
        if (channels.IsEmpty)
        {
            ((ICollection<KeyValuePair<long, ConcurrentDictionary<long, byte>>>)_voiceChannelsByClan)
                .Remove(new KeyValuePair<long, ConcurrentDictionary<long, byte>>(key.ClanId, channels));
        }
    }

    private Task OnVoiceLeavedAsync(VoiceLeavedEventEventData evt)
    {
        var voice = (VoiceLeavedEventResponse)evt;
        AdjustOccupancy(new VoiceKey(voice.ClanId, voice.VoiceChannelId), -1);
        return Task.CompletedTask;
    }

    private Task OnVoiceEndedAsync(VoiceEndedEventEventData evt)
    {
        var voice = (VoiceEndedEventResponse)evt;
        if (long.TryParse(voice.VoiceChannelId, out var channelId))
        {
            _voiceOccupancy.TryRemove(new VoiceKey(voice.ClanId, channelId), out _);
        }

        return Task.CompletedTask;
    }

    private void AdjustOccupancy(VoiceKey key, int delta)
    {
        while (true)
        {
            if (!_voiceOccupancy.TryGetValue(key, out var current))
            {
                if (delta > 0 && _voiceOccupancy.TryAdd(key, delta))
                {
                    return;
                }

                if (delta < 0)
                {
                    return;
                }

                continue;
            }

            var next = Math.Max(0, current + delta);
            if (next == 0)
            {
                _voiceOccupancy.TryRemove(key, out _);
                return;
            }

            if (_voiceOccupancy.TryUpdate(key, next, current))
            {
                return;
            }
        }
    }

}

