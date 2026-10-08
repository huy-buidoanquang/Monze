using System.Globalization;
using Google.Protobuf;
using Mezon.Net.Client;
using Mezon.Net.Core;
using Mezon.Net.Internal.Api;
using Mezon.Net.Internal.Realtime;

namespace Monze.Simulator;

/// <summary>
/// Inbound platform events, pushed to the bot exactly as the Mezon gateway
/// would: the world changes first, then the realtime envelope goes to every
/// bot session that joined the clan (or, for button and dropdown events, to
/// the bot's own stream). Each call returns once the frame reached the SDK
/// receive handler of every eligible session, so the SDK event is about to
/// fire; the bot's reaction is then awaited through the recorder.
/// </summary>
public sealed class SimInbound
{
    private readonly MezonSimulator _simulator;

    internal SimInbound(MezonSimulator simulator)
    {
        _simulator = simulator;
    }

    private SimWorld World => _simulator.World;

    /// <summary>A member writes <paramref name="text"/> in a channel, optionally replying and mentioning users.</summary>
    public Task<SimPush> SayAsync(
        long clanId,
        long channelId,
        long userId,
        string text,
        long? replyTo = null,
        IReadOnlyList<long>? mentions = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var channel = RequireChannel(clanId, channelId);
        RequireMember(clanId, userId);
        if (replyTo is long original)
        {
            var replied = World.FindMessage(original);
            if (replied is null || replied.Deleted || replied.ChannelId != channelId)
            {
                throw new ArgumentException($"Message {original} is not a live message of channel {channelId}.", nameof(replyTo));
            }
        }

        var message = World.StoreMessage(new SimMessage(
            World.NextId(),
            clanId,
            channel.Id,
            userId,
            MessageContent.CreateText(text).ToJson(),
            0,
            World.Time.GetUtcNow(),
            replyTo,
            mentions ?? [],
            false,
            null,
            0,
            false));
        var proto = _simulator.ToChannelMessage(message);
        if (mentions is { Count: > 0 })
        {
            // Mention positions point at "@username" in the text when present.
            var list = new MessageMentionList();
            foreach (var mentioned in mentions)
            {
                var username = World.FindUser(mentioned)?.Username ?? string.Empty;
                var start = username.Length == 0 ? -1 : text.IndexOf("@" + username, StringComparison.Ordinal);
                list.Mentions.Add(new MessageMention
                {
                    UserId = mentioned,
                    Username = username,
                    S = Math.Max(0, start),
                    E = start < 0 ? 0 : start + username.Length + 1
                });
            }

            proto.Mentions = list.ToByteString();
        }

        return _simulator.PushAsync(
            SimPushKind.ChannelMessage,
            new Envelope { ChannelMessage = proto },
            session => session.HasJoinedClan(clanId),
            new SimAction
            {
                Kind = SimActionKind.Push,
                Operation = nameof(SimPushKind.ChannelMessage),
                ClanId = clanId,
                ChannelId = channelId,
                MessageId = message.Id,
                TargetUserId = userId,
                ContentJson = message.ContentJson,
                ReplyToMessageId = replyTo
            });
    }

    /// <summary>
    /// A user clicks a button of a bot message. Only the receiver can click an
    /// ephemeral message unless <paramref name="allowInvisible"/> forges it.
    /// </summary>
    public Task<SimPush> ClickButtonAsync(
        long clanId,
        long channelId,
        long messageId,
        long userId,
        string buttonId,
        string? extraData = null,
        bool allowInvisible = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buttonId);
        var message = RequireInteractiveMessage(clanId, channelId, messageId, userId, allowInvisible);
        var click = new MessageButtonClicked
        {
            MessageId = messageId,
            ChannelId = channelId,
            ButtonId = buttonId,
            SenderId = message.SenderId,
            UserId = userId,
            ExtraData = extraData ?? string.Empty
        };
        return _simulator.PushAsync(
            SimPushKind.MessageButtonClicked,
            new Envelope { MessageButtonClicked = click },
            static _ => true,
            Draft(SimPushKind.MessageButtonClicked, clanId, channelId, messageId, userId));
    }

    /// <summary>A user picks values in a dropdown of a bot message.</summary>
    public Task<SimPush> SelectDropdownAsync(
        long clanId,
        long channelId,
        long messageId,
        long userId,
        string selectboxId,
        IReadOnlyList<string> values,
        bool allowInvisible = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectboxId);
        ArgumentNullException.ThrowIfNull(values);
        var message = RequireInteractiveMessage(clanId, channelId, messageId, userId, allowInvisible);
        var selected = new DropdownBoxSelected
        {
            MessageId = messageId,
            ChannelId = channelId,
            SelectboxId = selectboxId,
            SenderId = message.SenderId,
            UserId = userId
        };
        selected.Values.Add(values);
        return _simulator.PushAsync(
            SimPushKind.DropdownBoxSelected,
            new Envelope { DropdownBoxSelected = selected },
            static _ => true,
            Draft(SimPushKind.DropdownBoxSelected, clanId, channelId, messageId, userId));
    }

    /// <summary>
    /// A user joins the clan. An unknown user id is created first. The event
    /// reaches sessions that joined the clan, and every session when the bot
    /// itself was added.
    /// </summary>
    public Task<SimPush> UserAddedAsync(long clanId, long userId, bool isBot = false)
    {
        if (World.FindClan(clanId) is null)
        {
            throw new ArgumentException($"Clan {clanId} does not exist.", nameof(clanId));
        }

        var user = World.FindUser(userId);
        if (user is null || user.IsBot != isBot)
        {
            World.User(userId, user?.Username ?? $"user{userId}", user?.DisplayName, isBot, user?.Avatar);
            user = World.FindUser(userId)!;
        }

        var member = World.FindMember(clanId, userId) ?? World.AddMember(clanId, userId);
        var profile = new UserProfileRedis
        {
            UserId = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Avatar = user.Avatar,
            IsBot = user.IsBot,
            Online = true,
            CreateTimeSecond = (uint)member.JoinedAt.ToUnixTimeSeconds()
        };
        profile.JoinedClans.Add(clanId);
        var botId = World.Bot.Id;
        return _simulator.PushAsync(
            SimPushKind.AddClanUserEvent,
            new Envelope { AddClanUserEvent = new AddClanUserEvent { ClanId = clanId, User = profile, Invitor = string.Empty } },
            session => userId == botId || session.HasJoinedClan(clanId),
            Draft(SimPushKind.AddClanUserEvent, clanId, 0, 0, userId));
    }

    /// <summary>A member enters a voice room.</summary>
    public Task<SimPush> VoiceJoinAsync(long clanId, long channelId, long userId)
    {
        var channel = RequireVoice(clanId, channelId);
        World.JoinVoice(channelId, userId);
        var user = World.FindUser(userId)!;
        var joined = new VoiceJoinedEvent
        {
            ClanId = clanId,
            ClanName = World.FindClan(clanId)!.Name,
            Id = Guid.NewGuid().ToString("N"),
            Participant = user.DisplayName,
            UserId = userId,
            VoiceChannelLabel = channel.Label,
            VoiceChannelId = channelId
        };
        return _simulator.PushAsync(
            SimPushKind.VoiceJoinedEvent,
            new Envelope { VoiceJoinedEvent = joined },
            session => session.HasJoinedClan(clanId),
            Draft(SimPushKind.VoiceJoinedEvent, clanId, channelId, 0, userId));
    }

    /// <summary>A member leaves a voice room.</summary>
    public Task<SimPush> VoiceLeaveAsync(long clanId, long channelId, long userId)
    {
        RequireVoice(clanId, channelId);
        World.LeaveVoice(channelId, userId);
        var left = new VoiceLeavedEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            ClanId = clanId,
            VoiceChannelId = channelId,
            VoiceUserId = userId
        };
        return _simulator.PushAsync(
            SimPushKind.VoiceLeavedEvent,
            new Envelope { VoiceLeavedEvent = left },
            session => session.HasJoinedClan(clanId),
            Draft(SimPushKind.VoiceLeavedEvent, clanId, channelId, 0, userId));
    }

    /// <summary>The voice session of a room ends (everyone is removed).</summary>
    public Task<SimPush> VoiceEndAsync(long clanId, long channelId)
    {
        RequireVoice(clanId, channelId);
        World.EndVoice(channelId);
        var ended = new VoiceEndedEvent
        {
            Id = World.NextId(),
            ClanId = clanId,
            VoiceChannelId = channelId.ToString(CultureInfo.InvariantCulture)
        };
        return _simulator.PushAsync(
            SimPushKind.VoiceEndedEvent,
            new Envelope { VoiceEndedEvent = ended },
            session => session.HasJoinedClan(clanId),
            Draft(SimPushKind.VoiceEndedEvent, clanId, channelId, 0, 0));
    }

    /// <summary>A channel is created in the clan.</summary>
    public Task<SimPush> ChannelCreatedAsync(
        long clanId,
        long channelId,
        string label,
        int type = (int)ChannelType.Channel,
        bool isPrivate = false)
    {
        var channel = World.AddChannel(clanId, channelId, label, type, isPrivate);
        var created = new ChannelCreatedEvent
        {
            ClanId = clanId,
            CreatorId = channel.CreatorId,
            ChannelId = channelId,
            ChannelLabel = label,
            ChannelPrivate = isPrivate ? 1 : 0,
            ChannelType = type,
            ClanName = World.FindClan(clanId)!.Name
        };
        return _simulator.PushAsync(
            SimPushKind.ChannelCreatedEvent,
            new Envelope { ChannelCreatedEvent = created },
            session => session.HasJoinedClan(clanId),
            Draft(SimPushKind.ChannelCreatedEvent, clanId, channelId, 0, channel.CreatorId));
    }

    /// <summary>A channel changes; <paramref name="status"/> 3 is what Monze treats as "removed" for voice rooms.</summary>
    public Task<SimPush> ChannelUpdatedAsync(
        long clanId,
        long channelId,
        string? label = null,
        int? type = null,
        bool? isPrivate = null,
        int status = 0)
    {
        RequireChannel(clanId, channelId);
        var channel = World.UpdateChannel(channelId, label, type, isPrivate);
        var updated = new ChannelUpdatedEvent
        {
            ClanId = clanId,
            CreatorId = channel.CreatorId,
            ChannelId = channelId,
            ChannelLabel = channel.Label,
            ChannelType = channel.Type,
            ChannelPrivate = channel.IsPrivate,
            Status = status,
            MeetingCode = MezonSimulator.MeetingCode(channel),
            Active = 1
        };
        return _simulator.PushAsync(
            SimPushKind.ChannelUpdatedEvent,
            new Envelope { ChannelUpdatedEvent = updated },
            session => session.HasJoinedClan(clanId),
            Draft(SimPushKind.ChannelUpdatedEvent, clanId, channelId, 0, 0));
    }

    /// <summary>A channel is deleted.</summary>
    public Task<SimPush> ChannelDeletedAsync(long clanId, long channelId)
    {
        RequireChannel(clanId, channelId);
        World.RemoveChannel(channelId);
        var deleted = new ChannelDeletedEvent
        {
            ClanId = clanId,
            ChannelId = channelId,
            Deletor = World.FindClan(clanId)!.OwnerId.ToString(CultureInfo.InvariantCulture)
        };
        return _simulator.PushAsync(
            SimPushKind.ChannelDeletedEvent,
            new Envelope { ChannelDeletedEvent = deleted },
            session => session.HasJoinedClan(clanId),
            Draft(SimPushKind.ChannelDeletedEvent, clanId, channelId, 0, 0));
    }

    /// <summary>
    /// The peer of a direct-message channel (<see cref="SimWorld.DirectChannel"/>)
    /// writes to the bot. The message has clan id 0, stream mode Dm and is not
    /// public; it reaches every bot session (no ClanJoin is needed).
    /// </summary>
    public Task<SimPush> SayDirectAsync(long channelId, long userId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (World.DirectPeer(channelId) != userId)
        {
            throw new ArgumentException($"Channel {channelId} is not a direct-message channel of user {userId}.", nameof(channelId));
        }

        var message = World.StoreMessage(new SimMessage(
            World.NextId(),
            0,
            channelId,
            userId,
            MessageContent.CreateText(text).ToJson(),
            0,
            World.Time.GetUtcNow(),
            null,
            [],
            false,
            null,
            0,
            false));
        return _simulator.PushAsync(
            SimPushKind.ChannelMessage,
            new Envelope { ChannelMessage = _simulator.ToChannelMessage(message) },
            static _ => true,
            new SimAction
            {
                Kind = SimActionKind.Push,
                Operation = nameof(SimPushKind.ChannelMessage),
                ChannelId = channelId,
                MessageId = message.Id,
                TargetUserId = userId,
                ContentJson = message.ContentJson
            });
    }

    /// <summary>
    /// Sends a MessageButtonClicked exactly as a (possibly malicious) client
    /// can put it on the wire. mezon-api MessageButtonClick
    /// (server/api_interactive_message.go) forwards the client's event
    /// unchanged to the notification stream of <paramref name="senderId"/>:
    /// message, channel, user and button ids and extra_data are not checked
    /// (DropdownBoxSelected, in contrast, overwrites user_id with the session
    /// user). The event reaches the bot only when <paramref name="senderId"/>
    /// is the bot. Use <see cref="ClickButtonAsync"/> for an honest client.
    /// </summary>
    public Task<SimPush> ForgeButtonClickAsync(
        long channelId,
        long messageId,
        long senderId,
        long userId,
        string buttonId,
        string? extraData = null)
    {
        ArgumentNullException.ThrowIfNull(buttonId);
        var click = new MessageButtonClicked
        {
            MessageId = messageId,
            ChannelId = channelId,
            ButtonId = buttonId,
            SenderId = senderId,
            UserId = userId,
            ExtraData = extraData ?? string.Empty
        };
        var botId = World.Bot.Id;
        return _simulator.PushAsync(
            SimPushKind.MessageButtonClicked,
            new Envelope { MessageButtonClicked = click },
            _ => senderId == botId,
            Draft(SimPushKind.MessageButtonClicked, World.FindChannel(channelId)?.ClanId ?? 0, channelId, messageId, userId));
    }

    /// <summary>Delivers pushes held back by a reorder fault without waiting for the next push.</summary>
    public Task<int> ReleaseHeldPushesAsync() => _simulator.ReleaseHeldAsync();

    /// <summary>
    /// Closes every connected socket from the server side; the SDK raises
    /// Disconnected and Reconnecting and reconnects after about a second.
    /// Returns the number of sockets closed.
    /// </summary>
    public async Task<int> CloseSocketAsync()
    {
        var closed = 0;
        foreach (var session in _simulator.ConnectedSessions)
        {
            if (await session.CloseFromServerAsync("server close").ConfigureAwait(false))
            {
                closed++;
            }
        }

        return closed;
    }

    private static SimAction Draft(SimPushKind kind, long clanId, long channelId, long messageId, long userId)
        => new()
        {
            Kind = SimActionKind.Push,
            Operation = kind.ToString(),
            ClanId = clanId,
            ChannelId = channelId,
            MessageId = messageId,
            TargetUserId = userId
        };

    private SimChannel RequireChannel(long clanId, long channelId)
    {
        var channel = World.FindChannel(channelId);
        return channel is not null && channel.ClanId == clanId
            ? channel
            : throw new ArgumentException($"Channel {channelId} does not belong to clan {clanId}.", nameof(channelId));
    }

    private SimChannel RequireVoice(long clanId, long channelId)
    {
        var channel = RequireChannel(clanId, channelId);
        return channel.IsVoice
            ? channel
            : throw new ArgumentException($"Channel {channelId} is not a voice room.", nameof(channelId));
    }

    private void RequireMember(long clanId, long userId)
    {
        if (!World.IsMember(clanId, userId))
        {
            throw new ArgumentException($"User {userId} is not a member of clan {clanId}.", nameof(userId));
        }
    }

    private SimMessage RequireInteractiveMessage(long clanId, long channelId, long messageId, long userId, bool allowInvisible)
    {
        RequireChannel(clanId, channelId);
        RequireMember(clanId, userId);
        var message = World.FindMessage(messageId);
        if (message is null || message.Deleted || message.ChannelId != channelId)
        {
            throw new ArgumentException($"Message {messageId} is not a live message of channel {channelId}.", nameof(messageId));
        }

        if (!allowInvisible && message.EphemeralReceiverId is long receiver && receiver != userId)
        {
            throw new InvalidOperationException($"User {userId} cannot see ephemeral message {messageId} (receiver {receiver}).");
        }

        return message;
    }
}
