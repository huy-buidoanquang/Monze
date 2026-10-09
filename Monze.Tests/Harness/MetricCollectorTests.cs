using System.Diagnostics.Metrics;
using Monze.Testing;
using Monze.Testing.Harness;
using Xunit;

namespace Monze.Tests.Harness;

public sealed class MetricCollectorTests
{
    [Fact]
    [Req("REQ-HARN-001")]
    public void Aggregates_each_instrument_kind_per_tag_set()
    {
        var meterName = "Monze.Harness.Test." + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        using var ignored = new Meter(meterName + ".ignored");
        var depth = 7;
        using var collector = new MetricCollector(meterName);
        var commands = meter.CreateCounter<long>("commands");
        var inflight = meter.CreateUpDownCounter<int>("inflight");
        var duration = meter.CreateHistogram<double>("duration");
        var gauge = meter.CreateGauge<long>("depth.sync");
        meter.CreateObservableGauge("depth", () => depth);
        ignored.CreateCounter<long>("commands").Add(1_000);

        commands.Add(2, new KeyValuePair<string, object?>("outcome", "completed"), new KeyValuePair<string, object?>("module", "help"));
        commands.Add(3, new KeyValuePair<string, object?>("module", "help"), new KeyValuePair<string, object?>("outcome", "completed"));
        commands.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
        inflight.Add(1);
        inflight.Add(1);
        inflight.Add(-1);
        duration.Record(10.5);
        duration.Record(2);
        gauge.Record(4);
        gauge.Record(9);

        var first = collector.Snapshot();
        Assert.Equal(6, first.Sum("commands"));
        Assert.Equal(5, first.Sum("commands", "outcome=completed"));
        Assert.Equal(1, first.Sum("commands", "outcome=failed"));
        Assert.Equal(2, first.Values.Keys.Count(key => key.Instrument == "commands"));
        Assert.Equal(1, first.Sum("inflight"));
        Assert.Equal(2, first.Count("duration"));
        Assert.Equal(12.5, first.Sum("duration"));
        Assert.Equal(10.5, first.Max("duration"));
        Assert.Equal(9, first.Sum("depth.sync"));
        Assert.Equal(7, first.Sum("depth"));
        Assert.DoesNotContain(first.Values.Keys, key => key.Meter != meterName);

        commands.Add(4, new KeyValuePair<string, object?>("outcome", "failed"));
        inflight.Add(-1);
        depth = 0;
        var delta = collector.Snapshot().Since(first);
        Assert.Equal(4, delta.Sum("commands"));
        Assert.Equal(0, delta.Sum("commands", "outcome=completed"));
        Assert.Equal(-1, delta.Sum("inflight"));
        Assert.Equal(0, delta.Count("duration"));
        Assert.Equal(0, delta.Sum("depth"));
    }

    [Fact]
    [Req("REQ-HARN-001")]
    public void Runtime_meter_is_observed()
    {
        using var collector = new MetricCollector("System.Runtime");
        GC.Collect();
        var snapshot = collector.Snapshot();
        Assert.True(snapshot.Count("dotnet.gc.collections") > 0, string.Join(", ", snapshot.Values.Keys.Select(static key => key.Instrument).Distinct()));
        Assert.True(snapshot.Sum("dotnet.process.memory.working_set") > 0);
    }
}
