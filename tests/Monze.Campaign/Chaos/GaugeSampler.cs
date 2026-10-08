using Monze.Testing.Harness;

namespace Monze.Campaign.Chaos;

/// <summary>
/// Samples a few gauges of a <see cref="MetricCollector"/> every 50 ms in the
/// background and keeps each one's maximum (queue depths and pending writers
/// are only interesting at their peak).
/// </summary>
public sealed class GaugeSampler : IDisposable
{
    private readonly MetricCollector _collector;
    private readonly string[] _names;
    private readonly double[] _max;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public GaugeSampler(MetricCollector collector, params string[] names)
    {
        _collector = collector;
        _names = names;
        _max = new double[names.Length];
        _loop = Task.Run(SampleAsync);
    }

    public double Max(string name)
    {
        var index = Array.IndexOf(_names, name);
        lock (_max)
        {
            return index < 0 ? 0 : _max[index];
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }

    private async Task SampleAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var snapshot = _collector.Snapshot();
            lock (_max)
            {
                for (var i = 0; i < _names.Length; i++)
                {
                    _max[i] = Math.Max(_max[i], snapshot.Sum(_names[i]));
                }
            }

            try
            {
                await Task.Delay(50, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
