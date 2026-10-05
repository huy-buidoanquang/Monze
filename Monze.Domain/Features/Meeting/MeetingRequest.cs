namespace Monze.Domain;

public sealed record MeetingRequest(
    MeetingScheduleKind Kind,
    string? Name,
    string? WhenText,
    long? CancelScheduleId)
{
    public MeetingRequest(MeetingScheduleKind kind, string? whenText)
        : this(kind, null, whenText, null)
    {
    }

    public bool IsCancel => CancelScheduleId is not null;
}
