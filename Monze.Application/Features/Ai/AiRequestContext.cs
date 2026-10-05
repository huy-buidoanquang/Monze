namespace Monze.Application;

public sealed record AiRequestContext(
    long? ReplyToMessageId = null,
    string? HistoryText = null,
    bool ReplyWithinOneHour = true);
