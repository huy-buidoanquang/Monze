using Monze.Application;

namespace Monze.Tests;

internal sealed class MeetingMaintenanceTranscriptClient(TimeSpan delay) : ITranscriptClient
{
    private int _concurrency;
    private int _maxConcurrency;

    public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

    public async Task<AgentSummaryResult?> FetchSummaryAsync(
        string roomId,
        CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref _concurrency);
        UpdateMaximum(current);
        try
        {
            await Task.Delay(delay, cancellationToken);
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref _concurrency);
        }
    }

    private void UpdateMaximum(int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maxConcurrency);
            if (value <= current
                || Interlocked.CompareExchange(ref _maxConcurrency, value, current) == current)
            {
                return;
            }
        }
    }
}
