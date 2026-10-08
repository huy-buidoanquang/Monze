namespace Monze;

/// <summary>
/// Paces upstream sends at a steady rate with a small burst (a generic cell
/// rate algorithm on <see cref="TimeProvider"/> timestamps). Each reservation
/// gets its own slot in call order, so concurrent senders never race for
/// capacity, and an idle pacer banks at most <c>burst</c> slots. A rate of
/// zero or less disables pacing.
/// </summary>
internal sealed class UpstreamPacer
{
    private readonly TimeProvider _time;
    private readonly long _interval;
    private readonly long _credit;
    private readonly object _gate = new();
    private long _next;

    public UpstreamPacer(double perMinute, int burst, TimeProvider time)
    {
        _time = time;
        _interval = perMinute <= 0 ? 0 : (long)Math.Ceiling(time.TimestampFrequency * 60.0 / perMinute);
        _credit = _interval * (Math.Max(1, burst) - 1);
        _next = time.GetTimestamp() - _credit;
    }

    /// <summary>Reserves the next slot and returns how long to wait before sending.</summary>
    public TimeSpan Reserve()
    {
        if (_interval == 0)
        {
            return TimeSpan.Zero;
        }

        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var slot = Math.Max(_next, now - _credit);
            _next = slot + _interval;
            return slot <= now ? TimeSpan.Zero : TimeSpan.FromSeconds((double)(slot - now) / _time.TimestampFrequency);
        }
    }

    /// <summary>How many slots not yet reserved start within <paramref name="horizon"/> from now.</summary>
    public int Available(TimeSpan horizon)
    {
        if (_interval == 0)
        {
            return int.MaxValue;
        }

        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var slot = Math.Max(_next, now - _credit);
            var end = now + (long)(horizon.TotalSeconds * _time.TimestampFrequency);
            return slot > end ? 0 : (int)Math.Min(int.MaxValue, (end - slot) / _interval + 1);
        }
    }
}
