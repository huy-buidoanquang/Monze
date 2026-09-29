namespace Monze.Application;

public sealed record MeetingSessionBinding(
    long SessionId,
    long ClanId,
    long TextChannelId,
    long VoiceChannelId,
    bool DirectAgent,
    long? NotificationMessageId,
    long? NotificationChannelId = null);
