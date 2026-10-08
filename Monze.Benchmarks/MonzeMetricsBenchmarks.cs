using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using Mezon.Net.Core;
using Monze;
using Monze.Application.Commands;

/// <summary>
/// Cost of the instruments added for upstream rate limits and command
/// latency, measured while a MeterListener collects them (the realistic
/// case for an exporter). The target is 0 B/op.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class MonzeMetricsBenchmarks
{
    private static long _measurements;
    private readonly IRateLimitInfo _rateLimit = new BenchmarkRateLimit();
    private MeterListener _listener = null!;
    private string _module = null!;

    [GlobalSetup]
    public void Setup()
    {
        _measurements = 0;
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Monze")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => _measurements++);
        _listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => _measurements++);
        _listener.Start();
        _module = MonzeCommandMetricTags.Module(MonzeCommandNames.Meeting);
    }

    // A run where the listener saw nothing measured the no-listener path and
    // is not evidence for these benchmarks.
    [GlobalCleanup(Targets = [nameof(UpstreamRateLimitCallback), nameof(CommandInflightAndDuration)])]
    public void CleanupInstrumented()
    {
        _listener.Dispose();
        if (_measurements == 0)
        {
            throw new InvalidOperationException("MeterListener received no measurements.");
        }
    }

    [GlobalCleanup(Target = nameof(CommandModuleTag))]
    public void CleanupTag() => _listener.Dispose();

    [Benchmark]
    public Task UpstreamRateLimitCallback()
        => MonzeMetrics.RecordUpstreamRateLimit(_rateLimit);

    [Benchmark]
    public void CommandInflightAndDuration()
    {
        MonzeMetrics.CommandInflight.Add(1);
        MonzeMetrics.CommandInflight.Add(-1);
        MonzeMetrics.RecordCommand(_module, MonzeCommandMetricTags.Completed, TimeSpan.FromMilliseconds(3));
    }

    [Benchmark]
    public string CommandModuleTag()
        => MonzeCommandMetricTags.Module("AVA");

    private sealed class BenchmarkRateLimit : IRateLimitInfo
    {
        public bool IsGlobal => true;

        public int Limit => 500;

        public int Remaining => 0;

        public TimeSpan ResetAfter => TimeSpan.FromMilliseconds(20);

        public string Bucket => "transport-per-minute";

        public Func<long, long, string, Task>? SendBypassMessageAsync => null;
    }
}
