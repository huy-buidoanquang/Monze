namespace Monze.Application;

public interface IMeetingRepository
{
    Task<long> CreateMeetingAsync(long clanId, long channelId, long userId, long? eventId, CancellationToken cancellationToken);
    Task<IReadOnlySet<long>> ActiveVoiceClaimsAsync(long clanId, CancellationToken cancellationToken);
    Task<bool> SuggestMeetingAsync(long sessionId, long voiceChannelId, DateTimeOffset claimUntil, CancellationToken cancellationToken);
    Task<bool> BindMeetingRoomAsync(long clanId, long voiceChannelId, string roomId, CancellationToken cancellationToken);
    Task<bool> BindMeetingRoomByVoiceChannelAsync(long voiceChannelId, string roomId, CancellationToken cancellationToken);
    Task<MeetingSessionBinding?> BindAgentSessionAsync(long clanId, long voiceChannelId, string roomId, long requesterId, CancellationToken cancellationToken);
    Task<bool> TryClaimInboxAsync(string source, string eventKey, CancellationToken cancellationToken);
    Task ReleaseInboxAsync(string source, string eventKey, CancellationToken cancellationToken);
    Task PurgeInboxAsync(DateTimeOffset before, CancellationToken cancellationToken);
    Task<MeetingSessionBinding?> MarkMeetingEndedAsync(string roomId, CancellationToken cancellationToken);
    Task<bool> StoreSummaryAsync(
        string roomId,
        string summary,
        string? transcript,
        CancellationToken cancellationToken,
        string? leaseToken = null);
    Task MarkSummaryPendingAsync(string roomId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingMeetingSummary>> ListPendingSummariesAsync(int limit, CancellationToken cancellationToken);
    Task RecordSummaryRetryAsync(
        string roomId,
        string error,
        CancellationToken cancellationToken,
        string? leaseToken = null);
    Task<string?> LatestPostedSummaryAsync(long clanId, long voiceChannelId, CancellationToken cancellationToken);
    Task<MeetingSummaryRecord?> GetSummaryAsync(long clanId, long sessionId, CancellationToken cancellationToken);
    Task SetSessionNotificationMessageAsync(long sessionId, long channelId, long messageId, CancellationToken cancellationToken);
    Task ExpireSuggestedAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
