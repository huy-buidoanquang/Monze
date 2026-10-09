namespace Monze.Testing.Harness;

/// <summary>How a <see cref="MetricCollector"/> aggregates an instrument.</summary>
public enum MetricKind
{
    Counter,
    UpDownCounter,
    Histogram,
    Gauge,
    Observed
}
