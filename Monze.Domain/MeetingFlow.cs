namespace Monze.Domain;

public static class MeetingFlow
{
    public static MeetingStatus Suggest(MeetingStatus current)
        => current == MeetingStatus.Requested
            ? MeetingStatus.Suggested
            : throw new InvalidOperationException($"Cannot suggest from {current}.");

    public static bool TryBindRoom(MeetingStatus current, bool roomMatchesChannel, out MeetingStatus next)
    {
        if (roomMatchesChannel && current is MeetingStatus.Suggested or MeetingStatus.Live)
        {
            next = MeetingStatus.Live;
            return true;
        }

        next = current;
        return false;
    }

    public static MeetingStatus OnSummaryStored(MeetingStatus current)
        => current is MeetingStatus.Live or MeetingStatus.SummaryPending
            ? MeetingStatus.Posted
            : throw new InvalidOperationException($"Cannot post summary from {current}.");

    public static MeetingStatus OnSummaryFailed(MeetingStatus current)
        => current is MeetingStatus.Live or MeetingStatus.SummaryPending
            ? MeetingStatus.SummaryPending
            : throw new InvalidOperationException($"Cannot pend summary from {current}.");

    public static MeetingStatus ExpireSuggestion(MeetingStatus current)
        => current == MeetingStatus.Suggested ? MeetingStatus.Expired : current;
}
