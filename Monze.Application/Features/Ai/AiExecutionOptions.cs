namespace Monze.Application;

public sealed record AiExecutionOptions(
    int DailyTokenCap,
    int MaxInputCharacters,
    int MaxConcurrentRequests)
{
    public const int MaxOutputCharacters = 4_000;

    public static AiExecutionOptions Default { get; } = new(2_000, 8_000, 2);

    public AiExecutionOptions Normalize()
        => new(
            Math.Clamp(DailyTokenCap, 1, 10_000_000),
            Math.Clamp(MaxInputCharacters, 256, 1_000_000),
            Math.Clamp(MaxConcurrentRequests, 1, 128));
}
