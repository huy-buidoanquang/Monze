using System.Globalization;
using Monze.Application;
using Monze.Domain;

namespace Monze.Testing.Twin;

// Mirrors Monze.Infrastructure/Persistence/Meeting/PostgresSchedulingRepository.cs.
public sealed partial class InMemoryMonzeState
{
    private const string DefaultScheduleTitle = "Cuộc họp";
    private const int ClaimBatchSize = 128;
    private static readonly TimeSpan ScheduleLease = TimeSpan.FromSeconds(60);

    // meeting_schedule by id (BIGSERIAL).
    private readonly SortedDictionary<long, ScheduleRow> _schedules = new();
    private long _scheduleSequence;
    private long _leaseSequence;

    public Task<long> CreateMeetingScheduleAsync(
        long clanId,
        long channelId,
        long userId,
        string name,
        MeetingScheduleKind kind,
        string whenText,
        string timeZoneId,
        DateTimeOffset nextRunAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var next = ToTimestamptz(nextRunAt, nameof(nextRunAt));
        lock (_gate)
        {
            // The INSERT has no actor or clan check: any requester can create.
            var id = ++_scheduleSequence;
            _schedules.Add(id, new ScheduleRow
            {
                ClanId = clanId,
                ChannelId = channelId,
                RequesterId = userId,
                Title = string.IsNullOrWhiteSpace(name) ? DefaultScheduleTitle : name,
                Kind = kind.ToString(),
                WhenText = whenText,
                TimeZoneId = timeZoneId,
                NextRunAt = next
            });
            return Task.FromResult(id);
        }
    }

    public Task<IReadOnlyList<MeetingScheduleSummary>> ListMeetingSchedulesAsync(
        long clanId,
        long channelId,
        long userId,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var boundedLimit = Math.Clamp(limit, 1, 50);
        lock (_gate)
        {
            // The requester sees their own pending schedules in this channel;
            // the owner or a delegate of an active clan sees all of them.
            var seesAll = IsAdminLocked(clanId, userId);
            IReadOnlyList<MeetingScheduleSummary> rows = _schedules
                .Where(pair => pair.Value.ClanId == clanId
                    && pair.Value.ChannelId == channelId
                    && IsPendingSchedule(pair.Value.Status)
                    && (pair.Value.RequesterId == userId || seesAll))
                .OrderBy(static pair => pair.Value.NextRunAt)
                .ThenBy(static pair => pair.Key)
                .Take(boundedLimit)
                .Select(static pair => new MeetingScheduleSummary(
                    pair.Key,
                    pair.Value.Title,
                    ParseScheduleKind(pair.Value.Kind),
                    pair.Value.NextRunAt,
                    pair.Value.TimeZoneId,
                    pair.Value.RequesterId))
                .ToList();
            return Task.FromResult(rows);
        }
    }

    public Task<bool> CancelMeetingScheduleAsync(
        long clanId,
        long channelId,
        long userId,
        long scheduleId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // Same visibility as the list; a running (leased) schedule can be
            // cancelled and its lease is dropped.
            if (!_schedules.TryGetValue(scheduleId, out var row)
                || row.ClanId != clanId
                || row.ChannelId != channelId
                || !IsPendingSchedule(row.Status)
                || (row.RequesterId != userId && !IsAdminLocked(clanId, userId)))
            {
                return Task.FromResult(false);
            }

            row.Status = "cancelled";
            row.LockedUntil = null;
            row.LeaseToken = null;
            row.LastError = null;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<DueMeetingSchedule>> ClaimDueMeetingSchedulesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = NowLocked();
            var due = _schedules
                .Where(pair => (pair.Value.Status == "active"
                        || (pair.Value.Status == "running"
                            && pair.Value.LockedUntil is { } lockedUntil
                            && lockedUntil < now))
                    && pair.Value.NextRunAt <= now)
                .OrderBy(static pair => pair.Value.NextRunAt)
                .ThenBy(static pair => pair.Key)
                .Take(ClaimBatchSize)
                .ToList();
            var claimed = new List<DueMeetingSchedule>(due.Count);
            foreach (var (id, row) in due)
            {
                // md5(random() || clock_timestamp()) in SQL; a counter here
                // keeps runs deterministic with the same 32-hex shape.
                var leaseToken = (++_leaseSequence).ToString("x32", CultureInfo.InvariantCulture);
                row.Status = "running";
                row.LockedUntil = now + ScheduleLease;
                row.LeaseToken = leaseToken;
                claimed.Add(new DueMeetingSchedule(
                    id,
                    row.ClanId,
                    row.ChannelId,
                    row.RequesterId,
                    ParseScheduleKind(row.Kind),
                    row.WhenText,
                    row.TimeZoneId,
                    row.NextRunAt,
                    leaseToken,
                    row.Title));
            }

            return Task.FromResult<IReadOnlyList<DueMeetingSchedule>>(claimed);
        }
    }

    public Task CompleteMeetingScheduleAsync(
        long id,
        string leaseToken,
        DateTimeOffset? nextRunAt,
        bool failed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset? next = nextRunAt is { } value ? ToTimestamptz(value, nameof(nextRunAt)) : null;
        lock (_gate)
        {
            // WHERE id = @id AND lease_token = @lease: a NULL token (released
            // or cancelled) never matches. The status itself is not checked.
            if (!_schedules.TryGetValue(id, out var row)
                || row.LeaseToken is null
                || !string.Equals(row.LeaseToken, leaseToken, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            row.Status = failed || next is not null ? "active" : "completed";
            row.NextRunAt = next ?? row.NextRunAt;
            row.LockedUntil = null;
            row.LeaseToken = null;
        }

        return Task.CompletedTask;
    }

    private static bool IsPendingSchedule(string status)
        => status is "active" or "running";

    private static MeetingScheduleKind ParseScheduleKind(string value)
        => Enum.TryParse<MeetingScheduleKind>(value, true, out var parsed)
            ? parsed
            : MeetingScheduleKind.Once;

    private sealed class ScheduleRow
    {
        public required long ClanId { get; init; }

        public required long ChannelId { get; init; }

        public required long RequesterId { get; init; }

        public required string Title { get; init; }

        public required string Kind { get; init; }

        public required string WhenText { get; init; }

        public required string TimeZoneId { get; init; }

        public required DateTimeOffset NextRunAt { get; set; }

        public string Status { get; set; } = "active";

        public DateTimeOffset? LockedUntil { get; set; }

        public string? LeaseToken { get; set; }

        public string? LastError { get; set; }
    }
}
