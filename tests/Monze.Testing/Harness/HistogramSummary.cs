namespace Monze.Testing.Harness;

/// <summary>The figures a campaign report shows for one <see cref="LatencyHistogram"/>.</summary>
public sealed record HistogramSummary(
    long Count,
    long Min,
    double Mean,
    long P50,
    long P90,
    long P95,
    long P99,
    long P999,
    long Max);
