using Monze.Domain;

namespace Monze.Application;

public sealed record MeetingScheduleSummary(
    long Id,
    string Name,
    MeetingScheduleKind Kind,
    DateTimeOffset NextRunAt,
    string TimeZoneId,
    long RequesterId);
