using Mezon.Net.Core;

namespace Monze.Simulator;

/// <summary>
/// Thread-safe in-memory model of the Mezon platform state Monze reads and
/// changes: users, clans (with owner and welcome channel), channels, clan
/// membership with join time and roles, roles, voice occupancy and channel
/// messages. Seed it with the builder methods (<see cref="WithBot"/>,
/// <see cref="User"/>, <see cref="Clan"/>); the simulator answers API calls
/// from it and records every change the bot makes. The bot identity is a
/// member of every clan unless <see cref="SimClanBuilder.WithoutBot"/> is used.
/// </summary>
public sealed class SimWorld
{
    private const long GeneratedIdBase = 1_900_000_000_000_000_000L;
    private readonly object _gate = new();
    private readonly Dictionary<long, SimUser> _users = [];
    private readonly Dictionary<long, SimClan> _clans = [];
    private readonly Dictionary<long, SimChannel> _channels = [];
    private readonly Dictionary<(long ClanId, long UserId), SimMember> _members = [];
    private readonly Dictionary<long, SimRole> _roles = [];
    private readonly Dictionary<long, HashSet<long>> _voice = [];
    private readonly Dictionary<long, SimMessage> _messages = [];
    private long _nextId = GeneratedIdBase;
    private SimBotIdentity? _bot;

    public SimWorld(TimeProvider? time = null)
    {
        Time = time ?? TimeProvider.System;
    }

    /// <summary>Clock used for join times, message timestamps and session expiry.</summary>
    public TimeProvider Time { get; }

    /// <summary>The bot identity; set with <see cref="WithBot"/>.</summary>
    public SimBotIdentity Bot
    {
        get
        {
            lock (_gate)
            {
                return _bot ?? throw new InvalidOperationException("Call SimWorld.WithBot before using the bot identity.");
            }
        }
    }

    /// <summary>Sets the bot identity and makes the bot a member of every existing clan.</summary>
    public SimWorld WithBot(long id, string username, string token, string? displayName = null)
    {
        RequirePositive(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        lock (_gate)
        {
            _bot = new SimBotIdentity(id, username, displayName ?? username, token);
            _users[id] = new SimUser(id, username, displayName ?? username, true, string.Empty);
            foreach (var clan in _clans.Values)
            {
                _members.TryAdd((clan.Id, id), new SimMember(clan.Id, id, null, clan.CreatedAt, new HashSet<long>()));
            }
        }

        return this;
    }

    /// <summary>Adds or replaces a user account.</summary>
    public SimWorld User(long id, string username, string? displayName = null, bool isBot = false, string? avatar = null)
    {
        RequirePositive(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        lock (_gate)
        {
            _users[id] = new SimUser(id, username, displayName ?? username, isBot, avatar ?? string.Empty);
        }

        return this;
    }

    /// <summary>
    /// Adds a clan owned by <paramref name="ownerId"/> (an existing user, who
    /// becomes a member) and returns a builder for its channels, members and roles.
    /// </summary>
    public SimClanBuilder Clan(long id, string name, long ownerId)
    {
        RequirePositive(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (!_users.ContainsKey(ownerId))
            {
                throw new ArgumentException($"Owner {ownerId} must be added with User() first.", nameof(ownerId));
            }

            if (_clans.ContainsKey(id))
            {
                throw new ArgumentException($"Clan {id} already exists.", nameof(id));
            }

            var now = Time.GetUtcNow();
            _clans[id] = new SimClan(id, name, ownerId, 0, now);
            _members[(id, ownerId)] = new SimMember(id, ownerId, null, now, new HashSet<long>());
            if (_bot is not null)
            {
                _members.TryAdd((id, _bot.Id), new SimMember(id, _bot.Id, null, now, new HashSet<long>()));
            }
        }

        return new SimClanBuilder(this, id);
    }

    /// <summary>Returns a new id above every seeded id (messages, generated events).</summary>
    public long NextId() => Interlocked.Increment(ref _nextId);

    public IReadOnlyList<SimClan> Clans
    {
        get
        {
            lock (_gate)
            {
                return _clans.Values.OrderBy(static clan => clan.Id).ToList();
            }
        }
    }

    /// <summary>Clans the user is a member of, ordered by id.</summary>
    public IReadOnlyList<SimClan> ClansOf(long userId)
    {
        lock (_gate)
        {
            return _clans.Values
                .Where(clan => _members.ContainsKey((clan.Id, userId)))
                .OrderBy(static clan => clan.Id)
                .ToList();
        }
    }

    public SimUser? FindUser(long id)
    {
        lock (_gate)
        {
            return _users.GetValueOrDefault(id);
        }
    }

    public SimClan? FindClan(long id)
    {
        lock (_gate)
        {
            return _clans.GetValueOrDefault(id);
        }
    }

    public SimChannel? FindChannel(long id)
    {
        lock (_gate)
        {
            return _channels.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<SimChannel> ChannelsOf(long clanId)
    {
        lock (_gate)
        {
            return _channels.Values
                .Where(channel => channel.ClanId == clanId)
                .OrderBy(static channel => channel.Id)
                .ToList();
        }
    }

    public SimMember? FindMember(long clanId, long userId)
    {
        lock (_gate)
        {
            return _members.GetValueOrDefault((clanId, userId));
        }
    }

    public bool IsMember(long clanId, long userId)
    {
        lock (_gate)
        {
            return _members.ContainsKey((clanId, userId));
        }
    }

    public IReadOnlyList<SimMember> MembersOf(long clanId)
    {
        lock (_gate)
        {
            return _members.Values
                .Where(member => member.ClanId == clanId)
                .OrderBy(static member => member.UserId)
                .ToList();
        }
    }

    public SimRole? FindRole(long id)
    {
        lock (_gate)
        {
            return _roles.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<SimRole> RolesOf(long clanId)
    {
        lock (_gate)
        {
            return _roles.Values
                .Where(role => role.ClanId == clanId)
                .OrderBy(static role => role.Id)
                .ToList();
        }
    }

    /// <summary>Users currently in the voice room, ordered by id.</summary>
    public IReadOnlyList<long> VoiceOccupants(long channelId)
    {
        lock (_gate)
        {
            return _voice.TryGetValue(channelId, out var users)
                ? users.Order().ToList()
                : [];
        }
    }

    public SimMessage? FindMessage(long id)
    {
        lock (_gate)
        {
            return _messages.GetValueOrDefault(id);
        }
    }

    /// <summary>Every message of the channel (deleted ones included), ordered by id.</summary>
    public IReadOnlyList<SimMessage> MessagesIn(long channelId)
    {
        lock (_gate)
        {
            return _messages.Values
                .Where(message => message.ChannelId == channelId)
                .OrderBy(static message => message.Id)
                .ToList();
        }
    }

    /// <summary>Adds a member without raising an event (use SimInbound.UserAddedAsync for the event).</summary>
    public SimMember AddMember(long clanId, long userId, string? clanNick = null, DateTimeOffset? joinedAt = null, IEnumerable<long>? roleIds = null)
    {
        lock (_gate)
        {
            RequireClan(clanId);
            if (!_users.ContainsKey(userId))
            {
                throw new ArgumentException($"User {userId} must be added with User() first.", nameof(userId));
            }

            var roles = new HashSet<long>(roleIds ?? []);
            foreach (var roleId in roles)
            {
                if (!_roles.TryGetValue(roleId, out var role) || role.ClanId != clanId)
                {
                    throw new ArgumentException($"Role {roleId} does not belong to clan {clanId}.", nameof(roleIds));
                }
            }

            var member = new SimMember(clanId, userId, clanNick, joinedAt ?? Time.GetUtcNow(), roles);
            _members[(clanId, userId)] = member;
            return member;
        }
    }

    /// <summary>Removes a member without raising an event.</summary>
    public bool RemoveMember(long clanId, long userId)
    {
        lock (_gate)
        {
            return _members.Remove((clanId, userId));
        }
    }

    /// <summary>Adds a channel without raising an event (use SimInbound.ChannelCreatedAsync for the event).</summary>
    public SimChannel AddChannel(
        long clanId,
        long channelId,
        string label,
        int type = (int)ChannelType.Channel,
        bool isPrivate = false,
        long parentId = 0,
        long categoryId = 0)
    {
        RequirePositive(channelId, nameof(channelId));
        lock (_gate)
        {
            var clan = RequireClan(clanId);
            if (_channels.ContainsKey(channelId))
            {
                throw new ArgumentException($"Channel {channelId} already exists.", nameof(channelId));
            }

            var channel = new SimChannel(channelId, clanId, label, type, isPrivate, parentId, categoryId, clan.OwnerId);
            _channels[channelId] = channel;
            return channel;
        }
    }

    /// <summary>Changes a channel without raising an event.</summary>
    public SimChannel UpdateChannel(long channelId, string? label = null, int? type = null, bool? isPrivate = null)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(channelId, out var channel))
            {
                throw new ArgumentException($"Channel {channelId} does not exist.", nameof(channelId));
            }

            channel = channel with
            {
                Label = label ?? channel.Label,
                Type = type ?? channel.Type,
                IsPrivate = isPrivate ?? channel.IsPrivate
            };
            _channels[channelId] = channel;
            if (!channel.IsVoice)
            {
                _voice.Remove(channelId);
            }

            return channel;
        }
    }

    /// <summary>Removes a channel (and its voice occupancy) without raising an event.</summary>
    public SimChannel? RemoveChannel(long channelId)
    {
        lock (_gate)
        {
            if (!_channels.Remove(channelId, out var channel))
            {
                return null;
            }

            _voice.Remove(channelId);
            return channel;
        }
    }

    /// <summary>Adds a role to a clan.</summary>
    public SimRole AddRole(long clanId, long roleId, string title, bool active = true)
    {
        RequirePositive(roleId, nameof(roleId));
        lock (_gate)
        {
            RequireClan(clanId);
            var role = new SimRole(roleId, clanId, title, active);
            _roles[roleId] = role;
            return role;
        }
    }

    /// <summary>Sets the welcome channel ListClanDescs reports for the clan.</summary>
    public void SetWelcomeChannel(long clanId, long channelId)
    {
        lock (_gate)
        {
            var clan = RequireClan(clanId);
            _clans[clanId] = clan with { WelcomeChannelId = channelId };
        }
    }

    /// <summary>Puts a user into a voice room without raising an event.</summary>
    public void JoinVoice(long channelId, long userId)
    {
        lock (_gate)
        {
            var channel = RequireVoice(channelId);
            if (!_members.ContainsKey((channel.ClanId, userId)))
            {
                throw new ArgumentException($"User {userId} is not a member of clan {channel.ClanId}.", nameof(userId));
            }

            if (!_voice.TryGetValue(channelId, out var users))
            {
                users = [];
                _voice[channelId] = users;
            }

            users.Add(userId);
        }
    }

    /// <summary>Removes a user from a voice room without raising an event.</summary>
    public bool LeaveVoice(long channelId, long userId)
    {
        lock (_gate)
        {
            return _voice.TryGetValue(channelId, out var users) && users.Remove(userId);
        }
    }

    /// <summary>Empties a voice room without raising an event.</summary>
    public void EndVoice(long channelId)
    {
        lock (_gate)
        {
            _voice.Remove(channelId);
        }
    }

    /// <summary>
    /// Adds and removes role holders. Returns false (and changes nothing)
    /// when the role is not in the clan or a user is not a clan member.
    /// </summary>
    internal bool TryChangeRoleHolders(long clanId, long roleId, IReadOnlyList<long> add, IReadOnlyList<long> remove)
    {
        lock (_gate)
        {
            if (!_roles.TryGetValue(roleId, out var role) || role.ClanId != clanId)
            {
                return false;
            }

            foreach (var userId in add.Concat(remove))
            {
                if (!_members.ContainsKey((clanId, userId)))
                {
                    return false;
                }
            }

            foreach (var userId in add)
            {
                var member = _members[(clanId, userId)];
                _members[(clanId, userId)] = member with { RoleIds = new HashSet<long>(member.RoleIds) { roleId } };
            }

            foreach (var userId in remove)
            {
                var member = _members[(clanId, userId)];
                var roles = new HashSet<long>(member.RoleIds);
                roles.Remove(roleId);
                _members[(clanId, userId)] = member with { RoleIds = roles };
            }

            return true;
        }
    }

    internal SimMessage StoreMessage(SimMessage message)
    {
        lock (_gate)
        {
            _messages[message.Id] = message;
            return message;
        }
    }

    internal SimMessage? ReviseMessage(long messageId, string contentJson)
    {
        lock (_gate)
        {
            if (!_messages.TryGetValue(messageId, out var message) || message.Deleted)
            {
                return null;
            }

            message = message with { ContentJson = contentJson, Revision = message.Revision + 1 };
            _messages[messageId] = message;
            return message;
        }
    }

    internal SimMessage? DeleteMessage(long messageId)
    {
        lock (_gate)
        {
            if (!_messages.TryGetValue(messageId, out var message) || message.Deleted)
            {
                return null;
            }

            message = message with { Deleted = true };
            _messages[messageId] = message;
            return message;
        }
    }

    private SimClan RequireClan(long clanId)
        => _clans.TryGetValue(clanId, out var clan)
            ? clan
            : throw new ArgumentException($"Clan {clanId} does not exist.", nameof(clanId));

    private SimChannel RequireVoice(long channelId)
        => _channels.TryGetValue(channelId, out var channel) && channel.IsVoice
            ? channel
            : throw new ArgumentException($"Channel {channelId} is not a voice room.", nameof(channelId));

    private static void RequirePositive(long value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, "Ids must be positive.");
        }
    }
}
