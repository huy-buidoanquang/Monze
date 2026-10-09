using Microsoft.Extensions.Logging;

namespace Monze;

public sealed partial class MonzeBot
{
    // Commands and interactions run on SDK event tasks, not on the workers the
    // runtime drains, and the SDK gives them no cancellation token. They run
    // under this token and are counted, so a stop lets them record their
    // outcome before the client and the database go away (WF-08).
    private readonly CancellationTokenSource _handlerStop = new();
    private readonly TaskCompletionSource _handlersIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _handlersInFlight;
    private int _handlersDraining;

    /// <summary>
    /// Runs a command or interaction handler with the handler token. Once the
    /// bot is stopping no new handler starts: its message or click is not
    /// claimed, so a redelivery after the restart still runs it.
    /// </summary>
    private async Task RunHandlerAsync(Func<CancellationToken, Task> handler)
    {
        // Counted before the stop flag is read; DrainHandlersAsync sets the flag before
        // it reads the count, so either this handler is skipped or the drain waits for it.
        Interlocked.Increment(ref _handlersInFlight);
        try
        {
            if (Volatile.Read(ref _handlersDraining) == 0)
            {
                await handler(_handlerStop.Token);
            }
        }
        catch (Exception) when (_handlerStop.IsCancellationRequested)
        {
        }
        finally
        {
            if (Interlocked.Decrement(ref _handlersInFlight) == 0 && Volatile.Read(ref _handlersDraining) == 1)
            {
                _handlersIdle.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Lets running handlers finish for <see cref="MonzeWorkerTimings.UncertainMarkTimeout"/>,
    /// then cancels them (an AI request closes its loading card, WF-06) and
    /// waits as long again for them to record that.
    /// </summary>
    private async Task DrainHandlersAsync()
    {
        Interlocked.Exchange(ref _handlersDraining, 1);
        if (Volatile.Read(ref _handlersInFlight) == 0 || await WaitForHandlersAsync())
        {
            return;
        }

        await _handlerStop.CancelAsync();
        if (!await WaitForHandlersAsync())
        {
            _logger.LogWarning(
                "{Count} command or interaction handlers were still running when the bot stopped.",
                Volatile.Read(ref _handlersInFlight));
        }
    }

    private async Task<bool> WaitForHandlersAsync()
    {
        try
        {
            await _handlersIdle.Task.WaitAsync(_timings.UncertainMarkTimeout, _time);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
