using Monze.Domain;

namespace Monze.Application;

public interface ISchedulingRepository
{
    Task<long> CreateMeetingScheduleAsync(
        long clanId,
        long channelId,
        long userId,
        MeetingScheduleKind kind,
        string whenText,
        string timeZoneId,
        DateTimeOffset nextRunAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DueMeetingSchedule>> ClaimDueMeetingSchedulesAsync(CancellationToken cancellationToken);

    Task CompleteMeetingScheduleAsync(
        long id,
        string leaseToken,
        DateTimeOffset? nextRunAt,
        bool failed,
        CancellationToken cancellationToken);
}
