namespace Monze.Domain;

public enum OutboxKind
{
    CommandReply,
    Announcement,
    Reminder,
    MeetingSummary,
    EventPost
}
