using System.Globalization;
using Monze.Application;

namespace Monze.Testing.Twin;

// Mirrors CreateMeetingAsync, SuggestMeetingAsync and GetSummaryAsync of
// Monze.Infrastructure/Persistence/Meeting/PostgresMeetingRepository*.cs.
// Only the meeting_session columns those statements touch are modelled.
public sealed partial class InMemoryMonzeState
{
    // meeting_session by id (BIGSERIAL), voice_claim by (clan_id,
    // voice_channel_id) and meeting_summary by session_id.
    private readonly SortedDictionary<long, MeetingSessionRow> _sessions = new();
    private readonly Dictionary<(long ClanId, long VoiceChannelId), VoiceClaimRow> _voiceClaims = new();
    private readonly SortedDictionary<long, MeetingSummaryRow> _summaries = new();
    private long _sessionSequence;

    /// <summary>
    /// Seeds an ended meeting_session ('posted', or 'summary_pending' when
    /// <paramref name="posted"/> is false) with its meeting_summary row and
    /// returns the session id.
    /// </summary>
    public long AddMeetingSummary(
        long clanId,
        long requesterId,
        long textChannelId,
        long voiceChannelId,
        string summaryText,
        bool posted = true)
    {
        lock (_gate)
        {
            var now = NowLocked();
            var id = ++_sessionSequence;
            _sessions.Add(id, new MeetingSessionRow
            {
                ClanId = clanId,
                TextChannelId = textChannelId,
                RequesterId = requesterId,
                CreatedAt = now,
                Status = posted ? "posted" : "summary_pending",
                VoiceChannelId = voiceChannelId,
                RoomId = "twin-room-" + id.ToString(CultureInfo.InvariantCulture),
                StartedAt = now,
                EndedAt = now,
                ContextClosedAt = now
            });
            _summaries.Add(id, new MeetingSummaryRow { SummaryText = summaryText, Posted = posted });
            return id;
        }
    }

    public Task<long> CreateMeetingAsync(
        long clanId,
        long channelId,
        long userId,
        long? eventId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // No actor or clan check, and eventId is ignored by the INSERT.
            var id = ++_sessionSequence;
            _sessions.Add(id, new MeetingSessionRow
            {
                ClanId = clanId,
                TextChannelId = channelId,
                RequesterId = userId,
                CreatedAt = NowLocked(),
                Status = "requested"
            });
            return Task.FromResult(id);
        }
    }

    public Task<bool> SuggestMeetingAsync(
        long sessionId,
        long voiceChannelId,
        DateTimeOffset claimUntil,
        CancellationToken cancellationToken,
        string? voiceChannelLabel = null,
        string? meetingTitle = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var until = ToTimestamptz(claimUntil, nameof(claimUntil));
        lock (_gate)
        {
            var now = NowLocked();
            if (!_sessions.TryGetValue(sessionId, out var session) || session.Status != "requested")
            {
                // Transaction rolled back: nothing changes.
                return Task.FromResult(false);
            }

            session.Status = "suggested";
            session.VoiceChannelId = voiceChannelId;
            session.ClaimUntil = until;
            session.VoiceChannelLabel = voiceChannelLabel ?? session.VoiceChannelLabel;
            session.MeetingTitle = meetingTitle ?? session.MeetingTitle;

            // Close every other open root context on the same voice channel.
            foreach (var (id, other) in _sessions)
            {
                if (id != sessionId
                    && other.ClanId == session.ClanId
                    && other.VoiceChannelId == voiceChannelId
                    && other.RootSessionId is null
                    && other.ContextClosedAt is null)
                {
                    other.ContextClosedAt = now;
                }
            }

            // Expire the 'suggested' session that holds an expired claim.
            var key = (session.ClanId, voiceChannelId);
            _voiceClaims.TryGetValue(key, out var claim);
            if (claim is not null
                && claim.ExpiresAt < now
                && _sessions.TryGetValue(claim.SessionId, out var previous)
                && previous.Status == "suggested")
            {
                previous.Status = "expired";
                previous.ClaimUntil = null;
            }

            if (claim is null)
            {
                _voiceClaims.Add(key, new VoiceClaimRow { SessionId = sessionId, ExpiresAt = until });
                return Task.FromResult(true);
            }

            if (claim.ExpiresAt < now || claim.SessionId == sessionId)
            {
                claim.SessionId = sessionId;
                claim.ExpiresAt = until;
                return Task.FromResult(true);
            }

            // The claim belongs to another unexpired session. The SQL cancels
            // this session and COMMITS, so the contexts closed above stay
            // closed (PostgresMeetingRepository.cs).
            session.Status = "cancelled";
            session.VoiceChannelId = null;
            session.ClaimUntil = null;
            return Task.FromResult(false);
        }
    }

    public Task<MeetingSummaryRecord?> GetSummaryAsync(long clanId, long sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // Clan-scoped and only posted summaries; no actor check.
            if (!_sessions.TryGetValue(sessionId, out var session)
                || session.ClanId != clanId
                || !_summaries.TryGetValue(sessionId, out var summary)
                || !summary.Posted)
            {
                return Task.FromResult<MeetingSummaryRecord?>(null);
            }

            return Task.FromResult<MeetingSummaryRecord?>(new MeetingSummaryRecord(
                sessionId,
                session.VoiceChannelId ?? 0,
                session.TextChannelId,
                summary.SummaryText,
                session.StartedAt,
                session.EndedAt,
                session.NotificationMessageId,
                session.ClanId,
                session.RoomId,
                session.MeetingTitle,
                session.VoiceChannelLabel,
                summary.FullTranscriptJson));
        }
    }

    Task<IReadOnlySet<long>> IMeetingRepository.ActiveVoiceClaimsAsync(long clanId, CancellationToken cancellationToken)
        => throw NotModelled();

    Task<MeetingSessionBinding?> IMeetingRepository.BindAgentSessionAsync(
        long clanId,
        long voiceChannelId,
        string roomId,
        long requesterId,
        CancellationToken cancellationToken,
        string? voiceChannelLabel)
        => throw NotModelled();

    Task<bool> IMeetingRepository.TryClaimInboxAsync(string source, string eventKey, CancellationToken cancellationToken)
        => throw NotModelled();

    Task IMeetingRepository.ReleaseInboxAsync(string source, string eventKey, CancellationToken cancellationToken)
        => throw NotModelled();

    Task IMeetingRepository.PurgeInboxAsync(DateTimeOffset before, CancellationToken cancellationToken)
        => throw NotModelled();

    Task<MeetingSessionBinding?> IMeetingRepository.MarkMeetingEndedAsync(string roomId, CancellationToken cancellationToken)
        => throw NotModelled();

    Task<MeetingSummaryContext?> IMeetingRepository.GetSummaryContextAsync(string roomId, CancellationToken cancellationToken)
        => throw NotModelled();

    Task<bool> IMeetingRepository.StoreSummaryAsync(
        string roomId,
        string summary,
        string? transcript,
        CancellationToken cancellationToken,
        string? leaseToken)
        => throw NotModelled();

    Task<bool> IMeetingRepository.StoreSummaryAsync(
        string roomId,
        string summary,
        string? transcript,
        MeetingSummaryDelivery? delivery,
        CancellationToken cancellationToken,
        string? leaseToken)
        => throw NotModelled();

    Task IMeetingRepository.MarkSummaryPendingAsync(string roomId, CancellationToken cancellationToken)
        => throw NotModelled();

    Task<IReadOnlyList<PendingMeetingSummary>> IMeetingRepository.ListPendingSummariesAsync(
        int limit,
        CancellationToken cancellationToken)
        => throw NotModelled();

    Task IMeetingRepository.RecordSummaryRetryAsync(
        string roomId,
        string error,
        CancellationToken cancellationToken,
        string? leaseToken)
        => throw NotModelled();

    Task IMeetingRepository.SetSessionInvitationMessageAsync(
        long sessionId,
        long channelId,
        long messageId,
        CancellationToken cancellationToken)
        => throw NotModelled();

    Task IMeetingRepository.SetSessionStatusMessageAsync(
        long sessionId,
        long channelId,
        long messageId,
        CancellationToken cancellationToken)
        => throw NotModelled();

    Task IMeetingRepository.CloseMeetingContextAsync(long clanId, long voiceChannelId, CancellationToken cancellationToken)
        => throw NotModelled();

    Task IMeetingRepository.CloseStartedMeetingContextsAsync(CancellationToken cancellationToken)
        => throw NotModelled();

    Task IMeetingRepository.ExpireSuggestedAsync(DateTimeOffset now, CancellationToken cancellationToken)
        => throw NotModelled();

    private static NotSupportedException NotModelled()
        => new("Not modelled by the twin yet.");

    private sealed class MeetingSessionRow
    {
        public required long ClanId { get; init; }

        public required long TextChannelId { get; init; }

        public required long RequesterId { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required string Status { get; set; }

        public long? VoiceChannelId { get; set; }

        public string? RoomId { get; set; }

        public DateTimeOffset? ClaimUntil { get; set; }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? EndedAt { get; set; }

        public long? NotificationMessageId { get; set; }

        public long? RootSessionId { get; set; }

        public DateTimeOffset? ContextClosedAt { get; set; }

        public string? VoiceChannelLabel { get; set; }

        public string? MeetingTitle { get; set; }
    }

    private sealed class VoiceClaimRow
    {
        public long SessionId { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed class MeetingSummaryRow
    {
        public required string SummaryText { get; init; }

        public string? FullTranscriptJson { get; init; }

        public bool Posted { get; init; }
    }
}
