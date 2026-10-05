using Monze.Domain;

namespace Monze.Application;

public sealed record MeetingSessionBinding(
    long SessionId,
    long ClanId,
    long TextChannelId,
    long VoiceChannelId,
    bool DirectAgent,
    long? NotificationMessageId,
    long? NotificationChannelId = null,
    string? MeetingTitle = null,
    string? VoiceChannelLabel = null,
    long? SourceMessageId = null,
    long? RootSessionId = null,
    MeetingStatus Status = MeetingStatus.Live);
