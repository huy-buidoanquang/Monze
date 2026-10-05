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
    private async Task<MeetingVoiceCandidate?> PickVoiceAsync(
        ICommandContext context,
        long clanId,
        CancellationToken cancellationToken)
        => await PickVoiceAsync(context.Client, clanId, cancellationToken);

    private async Task<MeetingVoiceCandidate?> PickVoiceAsync(
        SdkMezonClient client,
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
        SdkMezonClient client,
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
                var candidate = await CreateVoiceCandidateAsync(
                    client,
                    channelId,
                    channels.Channeldesc[i].ChannelLabel,
                    cancellationToken);
                if (IsEmptyVoice(clanId, channelId))
                {
                    return candidate;
                }
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
                    var candidate = await CreateVoiceCandidateAsync(
                        client,
                        pair.Key,
                        null,
                        cancellationToken);
                    if (IsEmptyVoice(clanId, pair.Key))
                    {
                        return candidate;
                    }
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
        SdkMezonClient client,
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
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var version = _voiceSnapshots.BeginRefresh(clanId);
                var snapshot = await clan.ListChannelVoiceUsersAsync(
                    0,
                    (int)ChannelType.MezonVoice,
                    new RequestOptions { SocketSendTimeout = 5_000 });
                if (!_voiceSnapshots.IsCurrent(clanId, version))
                {
                    continue;
                }

                if (_voiceChannelsByClan.TryGetValue(clanId, out var knownVoiceChannels))
                {
                    foreach (var pair in knownVoiceChannels)
                    {
                        _voiceOccupancy.TryRemove(new VoiceKey(clanId, pair.Key), out _);
                    }
                }

                for (var i = 0; i < snapshot.VoiceChannelUsers.Count; i++)
                {
                    var voice = snapshot.VoiceChannelUsers[i];
                    var key = new VoiceKey(clanId, voice.ChannelId);
                    TrackVoiceChannel(key.ClanId, key.ChannelId);
                    _voiceOccupancy[key] = voice.UserIds.Count;
                }

                if (_voiceSnapshots.TryMarkReady(clanId, version))
                {
                    return true;
                }
            }

            _logger.LogWarning(
                "Voice occupancy changed repeatedly while refreshing clan {ClanId}; room selection was stopped.",
                clanId);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not refresh voice occupancy for clan {ClanId}.", clanId);
            return false;
        }
    }

    private bool IsEmptyVoice(long clanId, long channelId)
        => _voiceSnapshots.IsReady(clanId)
            && (!_voiceOccupancy.TryGetValue(new VoiceKey(clanId, channelId), out var count) || count == 0);

    private Task OnVoiceJoinedAsync(VoiceJoinedEventEventData evt)
    {
        var voice = (VoiceJoinedEventResponse)evt;
        _voiceSnapshots.Invalidate(voice.ClanId);
        TrackVoiceChannel(voice.ClanId, voice.VoiceChannelId);
        AdjustOccupancy(new VoiceKey(voice.ClanId, voice.VoiceChannelId), 1);
        return EnqueueMeetingAsync(MeetingIngressItem.VoiceProfile(
            voice.ClanId,
            voice.VoiceChannelId,
            voice.UserId,
            voice.Participant));
    }

    private Task OnChannelCreatedAsync(ChannelCreatedEventEventData evt)
    {
        var channel = (ChannelCreatedEventResponse)evt;
        if (channel.ChannelType == (int)ChannelType.MezonVoice)
        {
            _voiceSnapshots.Invalidate(channel.ClanId);
            TrackVoiceChannel(channel.ClanId, channel.ChannelId);
        }

        return Task.CompletedTask;
    }

    private Task OnChannelUpdatedAsync(ChannelUpdatedEventEventData evt)
    {
        var channel = (ChannelUpdatedEventResponse)evt;
        var key = new VoiceKey(channel.ClanId, channel.ChannelId);
        _voiceSnapshots.Invalidate(key.ClanId);
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
        _voiceSnapshots.Invalidate(key.ClanId);
        UntrackVoiceChannel(key);
        _voiceOccupancy.TryRemove(key, out _);
        return EnqueueMeetingContextCloseAsync(key.ClanId, key.ChannelId);
    }

    private void TrackVoiceChannel(long clanId, long channelId)
    {
        var channels = _voiceChannelsByClan.GetOrAdd(
            clanId,
            static _ => new ConcurrentDictionary<long, byte>());
        channels.TryAdd(channelId, 0);
        _voiceClanByChannel[channelId] = clanId;
    }

    private void UntrackVoiceChannel(VoiceKey key)
    {
        _voiceClanByChannel.TryRemove(key.ChannelId, out _);
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
        var key = new VoiceKey(voice.ClanId, voice.VoiceChannelId);
        _voiceSnapshots.Invalidate(key.ClanId);
        return AdjustOccupancy(key, -1)
            ? EnqueueMeetingContextCloseAsync(key.ClanId, key.ChannelId)
            : Task.CompletedTask;
    }

    private Task OnVoiceEndedAsync(VoiceEndedEventEventData evt)
    {
        var voice = (VoiceEndedEventResponse)evt;
        if (long.TryParse(voice.VoiceChannelId, out var channelId))
        {
            _voiceSnapshots.Invalidate(voice.ClanId);
            _voiceOccupancy.TryRemove(new VoiceKey(voice.ClanId, channelId), out _);
            return EnqueueMeetingContextCloseAsync(voice.ClanId, channelId);
        }

        return Task.CompletedTask;
    }

    private bool AdjustOccupancy(VoiceKey key, int delta)
    {
        while (true)
        {
            if (!_voiceOccupancy.TryGetValue(key, out var current))
            {
                if (delta > 0 && _voiceOccupancy.TryAdd(key, delta))
                {
                    return false;
                }

                if (delta < 0)
                {
                    return false;
                }

                continue;
            }

            var next = Math.Max(0, current + delta);
            if (next == 0)
            {
                if (_voiceOccupancy.TryRemove(
                        new KeyValuePair<VoiceKey, int>(key, current)))
                {
                    return true;
                }

                continue;
            }

            if (_voiceOccupancy.TryUpdate(key, next, current))
            {
                return false;
            }
        }
    }

}
