namespace Monze.Application.Commands;

public sealed record MonzeRateLimitOptions(
    int UserLimit,
    TimeSpan UserWindow,
    int AiLimit,
    TimeSpan AiWindow,
    int MeetingLimit,
    TimeSpan MeetingWindow,
    int AdminLimit,
    TimeSpan AdminWindow,
    int MaxEntries)
{
    public static MonzeRateLimitOptions Default { get; } = new(
        UserLimit: 8,
        UserWindow: TimeSpan.FromSeconds(10),
        AiLimit: 3,
        AiWindow: TimeSpan.FromMinutes(1),
        MeetingLimit: 2,
        MeetingWindow: TimeSpan.FromSeconds(10),
        AdminLimit: 5,
        AdminWindow: TimeSpan.FromMinutes(1),
        MaxEntries: 100_000);
}
