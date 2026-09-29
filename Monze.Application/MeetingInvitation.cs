namespace Monze.Application;

public sealed record MeetingInvitation(
    long VoiceChannelId,
    string VoiceChannelLabel,
    string? ConversationTitle = null,
    long? SessionId = null);
