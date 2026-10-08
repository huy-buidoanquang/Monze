using Mezon.Net.Core;

namespace Monze.Simulator;

/// <summary>
/// Fluent seeding of one clan of a <see cref="SimWorld"/>: channels, members,
/// roles, the welcome channel and initial voice occupancy. Seeding raises no
/// platform events; use SimInbound for changes Monze should observe live.
/// </summary>
public sealed class SimClanBuilder
{
    internal SimClanBuilder(SimWorld world, long clanId)
    {
        World = world;
        ClanId = clanId;
    }

    public SimWorld World { get; }

    public long ClanId { get; }

    /// <summary>Adds a text channel (ChannelType.Channel).</summary>
    public SimClanBuilder TextChannel(long id, string label, bool isPrivate = false)
        => Channel(id, label, (int)ChannelType.Channel, isPrivate);

    /// <summary>Adds a Mezon voice room (ChannelType.MezonVoice).</summary>
    public SimClanBuilder VoiceChannel(long id, string label)
        => Channel(id, label, (int)ChannelType.MezonVoice);

    /// <summary>Adds a channel of any SDK channel type.</summary>
    public SimClanBuilder Channel(long id, string label, int type, bool isPrivate = false, long parentId = 0, long categoryId = 0)
    {
        World.AddChannel(ClanId, id, label, type, isPrivate, parentId, categoryId);
        return this;
    }

    /// <summary>Adds a member (the user must exist) with optional nickname, join time and roles.</summary>
    public SimClanBuilder Member(long userId, string? clanNick = null, DateTimeOffset? joinedAt = null, params long[] roleIds)
    {
        World.AddMember(ClanId, userId, clanNick, joinedAt, roleIds);
        return this;
    }

    /// <summary>Adds a role.</summary>
    public SimClanBuilder Role(long id, string title, bool active = true)
    {
        World.AddRole(ClanId, id, title, active);
        return this;
    }

    /// <summary>Sets the welcome channel ListClanDescs reports.</summary>
    public SimClanBuilder WelcomeChannel(long channelId)
    {
        World.SetWelcomeChannel(ClanId, channelId);
        return this;
    }

    /// <summary>Puts a member into a voice room before the bot connects.</summary>
    public SimClanBuilder VoiceOccupant(long channelId, long userId)
    {
        World.JoinVoice(channelId, userId);
        return this;
    }

    /// <summary>Removes the bot from this clan (the bot is a member of every clan by default).</summary>
    public SimClanBuilder WithoutBot()
    {
        World.RemoveMember(ClanId, World.Bot.Id);
        return this;
    }
}
