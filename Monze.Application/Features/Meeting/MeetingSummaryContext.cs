namespace Monze.Application;

public sealed record MeetingSummaryContext(
    long SessionId,
    long ClanId,
    long VoiceChannelId,
    long TextChannelId,
    string RoomId,
    string? MeetingTitle,
    string? VoiceChannelLabel,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    long? NotificationMessageId,
    long? NotificationChannelId,
    long? SourceMessageId = null);
