namespace Monze.Application;

public readonly record struct InteractionInboxLease(
    long ClanId,
    long ChannelId,
    long MessageId,
    string Action,
    string Token);
