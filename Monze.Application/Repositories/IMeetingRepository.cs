namespace Monze.Application;

public interface IMeetingRepository
{
    Task<long> CreateMeetingAsync(long clanId, long channelId, long userId, long? eventId, CancellationToken cancellationToken);
    Task<IReadOnlySet<long>> ActiveVoiceClaimsAsync(long clanId, CancellationToken cancellationToken);
    Task<bool> SuggestMeetingAsync(
        long sessionId,
        long voiceChannelId,
        DateTimeOffset claimUntil,
        CancellationToken cancellationToken,
        string? voiceChannelLabel = null,
        string? meetingTitle = null);
    Task<MeetingSessionBinding?> BindAgentSessionAsync(
        long clanId,
        long voiceChannelId,
        string roomId,
        long requesterId,
        CancellationToken cancellationToken,
        string? voiceChannelLabel = null);
    Task<bool> TryClaimInboxAsync(string source, string eventKey, CancellationToken cancellationToken);
    Task ReleaseInboxAsync(string source, string eventKey, CancellationToken cancellationToken);
    Task PurgeInboxAsync(DateTimeOffset before, CancellationToken cancellationToken);
    Task<MeetingSessionBinding?> MarkMeetingEndedAsync(string roomId, CancellationToken cancellationToken);
    Task<MeetingSummaryContext?> GetSummaryContextAsync(string roomId, CancellationToken cancellationToken);
    Task<bool> StoreSummaryAsync(
        string roomId,
        string summary,
        string? transcript,
        CancellationToken cancellationToken,
        string? leaseToken = null);
    Task<bool> StoreSummaryAsync(
        string roomId,
        string summary,
        string? transcript,
        MeetingSummaryDelivery? delivery,
        CancellationToken cancellationToken,
        string? leaseToken = null);
    Task MarkSummaryPendingAsync(string roomId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingMeetingSummary>> ListPendingSummariesAsync(int limit, CancellationToken cancellationToken);
    Task RecordSummaryRetryAsync(
        string roomId,
        string error,
        CancellationToken cancellationToken,
        string? leaseToken = null);
    Task<MeetingSummaryRecord?> GetSummaryAsync(long clanId, long sessionId, CancellationToken cancellationToken);
    Task SetSessionInvitationMessageAsync(long sessionId, long channelId, long messageId, CancellationToken cancellationToken);
    Task SetSessionStatusMessageAsync(long sessionId, long channelId, long messageId, CancellationToken cancellationToken);
    Task CloseMeetingContextAsync(long clanId, long voiceChannelId, CancellationToken cancellationToken);
    Task CloseStartedMeetingContextsAsync(CancellationToken cancellationToken);
    Task ExpireSuggestedAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
