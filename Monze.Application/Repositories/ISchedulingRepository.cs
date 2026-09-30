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

    Task CompleteMeetingScheduleAsync(
        long id,
        string leaseToken,
        DateTimeOffset? nextRunAt,
        bool failed,
        CancellationToken cancellationToken);
}
