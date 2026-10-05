namespace Monze.Application;

public sealed record MeetingSummaryPresentation(
    string Title,
    string ActionItemsTitle,
    string Description,
    string Participants,
    string Content,
    string TranscriptUrl,
    IReadOnlyList<MeetingSummaryActionItem> ActionItems);
