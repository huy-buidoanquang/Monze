using System.Diagnostics.Metrics;
using Mezon.Net.Core;
using Monze.Application.Commands;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Hosting;

public sealed class MonzeMetricsTests
{
    [Fact]
    [Req("REQ-OBS-001")]
    public void Upstream_rate_limit_callback_records_the_bucket_and_delay()
    {
        using var capture = new MeasurementCapture("monze.upstream.ratelimit.delayed", "monze.upstream.ratelimit.delay");

        MonzeMetrics.RecordUpstreamRateLimit(new RateLimit("transport-per-minute", TimeSpan.FromMilliseconds(1_500)));

        Assert.Contains(capture.Measurements, item =>
            item.Instrument == "monze.upstream.ratelimit.delayed" && item.Value == 1 && item.Tags == "bucket=transport-per-minute");
        Assert.Contains(capture.Measurements, item =>
            item.Instrument == "monze.upstream.ratelimit.delay" && item.Value == 1_500 && item.Tags == "bucket=transport-per-minute");
    }

    [Fact]
    [Req("REQ-OBS-002")]
    public void Command_duration_is_tagged_with_module_and_outcome()
    {
        using var capture = new MeasurementCapture("monze.command.duration");

        MonzeMetrics.RecordCommand(
            MonzeCommandMetricTags.Module("AVA"),
            MonzeCommandMetricTags.Completed,
            TimeSpan.FromMilliseconds(42));

        var measurement = Assert.Single(capture.Measurements);
        Assert.Equal(42, measurement.Value);
        Assert.Equal("module=avatar,outcome=completed", measurement.Tags);
    }

    [Theory]
    [Req("REQ-OBS-002")]
    [InlineData("meeting", MonzeCommandNames.Meeting)]
    [InlineData("HELP", MonzeCommandNames.Help)]
    [InlineData("avt", MonzeCommandNames.Avatar)]
    [InlineData("translate", MonzeCommandNames.Translate)]
    [InlineData("drop table users", MonzeCommandNames.Unknown)]
    [InlineData("", MonzeCommandNames.Unknown)]
    [InlineData(null, MonzeCommandNames.Unknown)]
    public void Command_module_tag_is_bounded(string? command, string expected)
    {
        Assert.Equal(expected, MonzeCommandMetricTags.Module(command));
    }

    [Fact]
    [Req("REQ-OBS-003", "REQ-PERF-001")]
    public void New_instruments_do_not_allocate_while_a_listener_is_collecting()
    {
        using var capture = new MeasurementCapture(
            "monze.upstream.ratelimit.delayed",
            "monze.upstream.ratelimit.delay",
            "monze.command.inflight",
            "monze.command.duration")
        {
            Record = false
        };
        var info = new RateLimit("transport-per-second", TimeSpan.FromMilliseconds(20));
        var module = MonzeCommandMetricTags.Module(MonzeCommandNames.Meeting);
        for (var i = 0; i < 1_000; i++)
        {
            RecordAll(info, module);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            RecordAll(info, module);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(capture.Count >= 11_000 * 4);
    }

    private static void RecordAll(IRateLimitInfo info, string module)
    {
        _ = MonzeMetrics.RecordUpstreamRateLimit(info);
        MonzeMetrics.CommandInflight.Add(1);
        MonzeMetrics.CommandInflight.Add(-1);
        MonzeMetrics.RecordCommand(module, MonzeCommandMetricTags.Completed, TimeSpan.FromMilliseconds(3));
    }

    private sealed class RateLimit(string bucket, TimeSpan resetAfter) : IRateLimitInfo
    {
        public bool IsGlobal => true;

        public int Limit => 500;

        public int Remaining => 0;

        public TimeSpan ResetAfter => resetAfter;

        public string Bucket => bucket;

        public Func<long, long, string, Task>? SendBypassMessageAsync => null;
    }

    /// <summary>Listens to named Monze instruments on the current thread only.</summary>
    private sealed class MeasurementCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly HashSet<string> _names;
        private readonly int _threadId = Environment.CurrentManagedThreadId;

        public MeasurementCapture(params string[] names)
        {
            _names = new HashSet<string>(names, StringComparer.Ordinal);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Monze" && _names.Contains(instrument.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => OnMeasurement(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => OnMeasurement(instrument, value, tags));
            _listener.Start();
        }

        public bool Record { get; init; } = true;

        public int Count { get; private set; }

        public List<(string Instrument, double Value, string Tags)> Measurements { get; } = [];

        public void Dispose() => _listener.Dispose();

        private void OnMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (Environment.CurrentManagedThreadId != _threadId)
            {
                return;
            }

            Count++;
            if (!Record)
            {
                return;
            }

            var parts = new List<string>();
            foreach (var tag in tags)
            {
                parts.Add($"{tag.Key}={tag.Value}");
            }

            Measurements.Add((instrument.Name, value, string.Join(",", parts)));
        }
    }
}
