namespace Monze.Domain;

public sealed record MeetingRequest(MeetingScheduleKind Kind, string? WhenText);
