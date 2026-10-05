namespace Monze.Application;

public sealed record DueOutbox(
    long Id,
    long ChannelId,
    string Kind,
    string Body,
    int Attempts,
    long? ExternalMessageId,
    string LeaseToken,
    DateTimeOffset DueAt,
    bool MentionEveryone = false,
    string? ContentJson = null,
    long? MeetingSessionId = null,
    long? ReplyToMessageId = null,
    bool? ReplyDirectAgent = null,
    long? ReplyVoiceChannelId = null,
    string? ReplyVoiceChannelLabel = null);
