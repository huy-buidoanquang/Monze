using Monze.Domain;

namespace Monze.Application;

public interface ISchedulingRepository
{
    Task<long> CreateMeetingScheduleAsync(
        long clanId,
        long channelId,
        long userId,
        string name,
        MeetingScheduleKind kind,
        string whenText,
        string timeZoneId,
        DateTimeOffset nextRunAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MeetingScheduleSummary>> ListMeetingSchedulesAsync(
        long clanId,
        long channelId,
        long userId,
        int limit,
        CancellationToken cancellationToken);

    Task<bool> CancelMeetingScheduleAsync(
        long clanId,
        long channelId,
        long userId,
        long scheduleId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DueMeetingSchedule>> ClaimDueMeetingSchedulesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ends a claim: active again at <paramref name="nextRunAt"/> (or completed
    /// when it is null and not <paramref name="failed"/>). The schedule's
    /// last_error becomes <paramref name="errorCode"/> (null clears it).
    /// </summary>
    Task CompleteMeetingScheduleAsync(
        long id,
        string leaseToken,
        DateTimeOffset? nextRunAt,
        bool failed,
        CancellationToken cancellationToken,
        string? errorCode = null);
}
