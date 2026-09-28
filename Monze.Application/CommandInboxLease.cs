namespace Monze.Application;

public readonly record struct CommandInboxLease(
    long ClanId,
    long ChannelId,
    long MessageId,
    string Token);
