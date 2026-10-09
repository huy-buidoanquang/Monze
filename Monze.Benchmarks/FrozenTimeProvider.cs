/// <summary>A clock that never moves, so a benchmark measures one path of the rate limiter for its whole run.</summary>
internal sealed class FrozenTimeProvider : TimeProvider
{
    public static FrozenTimeProvider Instance { get; } = new();

    public override long GetTimestamp() => 0;
}
