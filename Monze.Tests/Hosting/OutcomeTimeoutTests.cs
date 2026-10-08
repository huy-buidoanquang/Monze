using Monze.Testing;
using Xunit;

namespace Monze.Tests.Hosting;

/// <summary>
/// Recording an outcome is never cut short while the bot runs (a slow
/// database must not turn a delivered message into an uncertain one, chaos
/// PG-04b) and is bounded once the bot stops (WF-05, WF-08).
/// </summary>
public sealed class OutcomeTimeoutTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    [Req("REQ-OUT-001")]
    public void It_is_not_cancelled_while_the_bot_runs_and_is_bounded_from_the_stop()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var stopping = new CancellationTokenSource();
        using var outcome = new OutcomeTimeout(stopping.Token, Timeout, time);

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(outcome.Token.IsCancellationRequested);

        stopping.Cancel();
        time.Advance(Timeout - TimeSpan.FromMilliseconds(1));
        Assert.False(outcome.Token.IsCancellationRequested);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(outcome.Token.IsCancellationRequested);
    }

    [Fact]
    [Req("REQ-OUT-001")]
    public void One_started_while_stopping_is_bounded_from_its_start()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        time.Advance(TimeSpan.FromMinutes(1));
        using var outcome = new OutcomeTimeout(stopping.Token, Timeout, time);

        time.Advance(Timeout - TimeSpan.FromMilliseconds(1));
        Assert.False(outcome.Token.IsCancellationRequested);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(outcome.Token.IsCancellationRequested);
    }

    [Fact]
    public void Disposing_it_before_the_stop_leaves_nothing_registered()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var stopping = new CancellationTokenSource();
        var outcome = new OutcomeTimeout(stopping.Token, Timeout, time);

        outcome.Dispose();
        stopping.Cancel();

        Assert.Equal(0, time.ActiveTimerCount);
    }
}
