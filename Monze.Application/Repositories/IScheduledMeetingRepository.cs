namespace Monze.Application;

public interface IScheduledMeetingRepository
{
    Task<bool> CommitScheduledMeetingAsync(
        DueMeetingSchedule schedule,
        long voiceChannelId,
        DateTimeOffset claimUntil,
        DateTimeOffset? nextRunAt,
        string announcementBody,
        CancellationToken cancellationToken);
}
