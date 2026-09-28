namespace Monze.Application;

public sealed record DueOutbox(
    long Id,
    long ChannelId,
    string Kind,
    string Body,
    int Attempts,
    long? ExternalMessageId,
    string LeaseToken,
    DateTimeOffset DueAt);
