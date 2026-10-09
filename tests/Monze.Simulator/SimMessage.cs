namespace Monze.Simulator;

/// <summary>
/// A channel message held by the simulated platform: who sent it, its content
/// JSON, the message it replies to, mentions, the ephemeral receiver (only
/// that user can see it) and whether it was later updated or deleted.
/// </summary>
public sealed record SimMessage(
    long Id,
    long ClanId,
    long ChannelId,
    long SenderId,
    string ContentJson,
    int Code,
    DateTimeOffset CreatedAt,
    long? ReplyToMessageId,
    IReadOnlyList<long> MentionUserIds,
    bool MentionEveryone,
    long? EphemeralReceiverId,
    int Revision,
    bool Deleted)
{
    /// <summary>Whether the content changed after the first send.</summary>
    public bool Updated => Revision > 0;

    /// <summary>Whether only <see cref="EphemeralReceiverId"/> can see the message.</summary>
    public bool IsEphemeral => EphemeralReceiverId is not null;
}
