namespace Monze;

internal readonly record struct MessageGapIngressItem(
    long ClanId,
    long ChannelId,
    long MessageId);
