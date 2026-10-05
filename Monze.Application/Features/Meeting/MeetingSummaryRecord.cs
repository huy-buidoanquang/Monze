namespace Monze.Application;

public sealed record MeetingSummaryRecord(
    long SessionId,
    long VoiceChannelId,
    long TextChannelId,
    string Summary,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    long? NotificationMessageId,
    long ClanId,
    string? RoomId = null,
    string? MeetingTitle = null,
    string? VoiceChannelLabel = null,
    string? FullTranscriptJson = null);
