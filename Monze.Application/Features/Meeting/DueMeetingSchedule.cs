using Monze.Domain;

namespace Monze.Application;

public sealed record DueMeetingSchedule(
    long Id,
    long ClanId,
    long ChannelId,
    long RequesterId,
    MeetingScheduleKind Kind,
    string WhenText,
    string TimeZoneId,
    DateTimeOffset NextRunAt,
    string LeaseToken,
    string Name = "Cuộc họp",
    string? LastError = null);
