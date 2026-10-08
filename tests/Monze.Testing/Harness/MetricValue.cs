namespace Monze.Testing.Harness;

/// <summary>
/// The aggregate of one metric series. <see cref="Sum"/> is the running total
/// of a counter, up-down counter or histogram, and the last value of a gauge
/// or observed instrument.
/// </summary>
public sealed record MetricValue(MetricKind Kind, long Count, double Sum, double Min, double Max);
