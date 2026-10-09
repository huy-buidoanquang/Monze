namespace Monze.Testing;

/// <summary>
/// A controllable <see cref="TimeProvider"/>: wall clock and monotonic
/// timestamp only move when the test calls <see cref="Advance"/>, and timers
/// (including Task.Delay(TimeSpan, TimeProvider)) fire when their due time is
/// reached. <see cref="JumpWallClock"/> moves only the wall clock, for clock
/// jump scenarios.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _utcNow;
    private long _timestampTicks;

    public ManualTimeProvider(DateTimeOffset start)
    {
        _utcNow = start;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestampTicks;
        }
    }

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "Use JumpWallClock for backward jumps.");
        }

        lock (_gate)
        {
            _utcNow += delta;
            _timestampTicks += delta.Ticks;
        }

        FireDueTimers();
    }

    public void JumpWallClock(TimeSpan delta)
    {
        lock (_gate)
        {
            _utcNow += delta;
        }
    }

    public int ActiveTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(static timer => timer.DueTimestamp is not null);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        FireDueTimers();
        return timer;
    }

    private void FireDueTimers()
    {
        while (true)
        {
            ManualTimer? due = null;
            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (timer.DueTimestamp is { } at && at <= _timestampTicks
                        && (due is null || at < due.DueTimestamp))
                    {
                        due = timer;
                    }
                }

                if (due is null)
                {
                    return;
                }

                due.DueTimestamp = due.PeriodTicks is { } period && period > 0
                    ? due.DueTimestamp + period
                    : null;
            }

            due.Fire();
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public long? DueTimestamp { get; set; }

        public long? PeriodTicks { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                DueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : _owner._timestampTicks + Math.Max(0, dueTime.Ticks);
                PeriodTicks = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero
                    ? null
                    : period.Ticks;
            }

            _owner.FireDueTimers();
            return true;
        }

        public void Fire() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._gate)
            {
                DueTimestamp = null;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
