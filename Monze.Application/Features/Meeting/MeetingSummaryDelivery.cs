namespace Monze.Application;

public sealed record MeetingSummaryDelivery(
    string SummaryContentJson,
    string ActionItemsContentJson);
