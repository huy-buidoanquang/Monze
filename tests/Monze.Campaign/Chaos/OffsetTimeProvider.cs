namespace Monze.Campaign.Chaos;

/// <summary>
/// The system clock shifted by <see cref="Offset"/>, for clock-jump and
/// app/database skew scenarios. Only the wall clock moves; timestamps stay
/// monotonic, like a real NTP step.
/// </summary>
public sealed class OffsetTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public TimeSpan Offset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        set => Interlocked.Exchange(ref _offsetTicks, value.Ticks);
    }

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
}
