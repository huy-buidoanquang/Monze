using Mezon.Net.Core;

namespace Monze.Simulator;

/// <summary>
/// A clan channel. <see cref="Type"/> uses the SDK <see cref="ChannelType"/>
/// values (1 text channel, 10 Mezon voice, 7 thread, ...).
/// </summary>
public sealed record SimChannel(
    long Id,
    long ClanId,
    string Label,
    int Type,
    bool IsPrivate,
    long ParentId,
    long CategoryId,
    long CreatorId)
{
    /// <summary>Whether this is a Mezon voice room.</summary>
    public bool IsVoice => Type == (int)ChannelType.MezonVoice;

    /// <summary>The stream mode the SDK derives for this channel (ChannelModeConverter).</summary>
    public int StreamMode => ChannelModeConverter.ToStreamMode(Type);
}
