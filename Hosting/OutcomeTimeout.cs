namespace Monze;

/// <summary>
/// The token for recording how a send, command or interaction ended (an
/// outbox completion, an inbox completion or an uncertain mark). It is not
/// cancelled while the bot runs, so a slow database does not turn a delivered
/// message or an answered command into an uncertain one; once the bot is
/// stopping, the call gets <see cref="MonzeWorkerTimings.UncertainMarkTimeout"/>
/// from the stop, or from its own start if that is later.
/// </summary>
internal sealed class OutcomeTimeout : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenRegistration _stopping;

    public OutcomeTimeout(CancellationToken stopping, TimeSpan timeout, TimeProvider time)
    {
        _timeout = timeout;
        _source = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);

        // Runs at once when the bot is already stopping.
        _stopping = stopping.Register(static state => ((OutcomeTimeout)state!).StartCountdown(), this);
    }

    public CancellationToken Token => _source.Token;

    public void Dispose()
    {
        // Unregisters first: Dispose waits for a running callback, which then never sees a disposed source.
        _stopping.Dispose();
        _source.Dispose();
    }

    private void StartCountdown() => _source.CancelAfter(_timeout);
}
