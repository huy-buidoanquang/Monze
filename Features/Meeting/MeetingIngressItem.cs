using Mezon.Net.Sdk.Agent;

namespace Monze;

internal readonly record struct MeetingIngressItem(
    MeetingIngressKind Kind,
    AgentSseSessionEvent? AgentEvent,
    AgentEventKind AgentKind,
    long ClanId,
    long VoiceChannelId,
    long UserId,
    string? ParticipantLabel)
{
    internal static MeetingIngressItem FromAgent(
        AgentSseSessionEvent agentEvent,
        AgentEventKind agentKind)
        => new(MeetingIngressKind.AgentEvent, agentEvent, agentKind, 0, 0, 0, null);

    internal static MeetingIngressItem VoiceEmpty(long clanId, long voiceChannelId)
        => new(MeetingIngressKind.VoiceEmpty, null, default, clanId, voiceChannelId, 0, null);

    internal static MeetingIngressItem VoiceProfile(
        long clanId,
        long voiceChannelId,
        long userId,
        string? participantLabel)
        => new(
            MeetingIngressKind.VoiceProfile,
            null,
            default,
            clanId,
            voiceChannelId,
            userId,
            participantLabel);

    internal static MeetingIngressItem RealtimeReset()
        => new(MeetingIngressKind.RealtimeReset, null, default, 0, 0, 0, null);
}
