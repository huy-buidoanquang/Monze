namespace Monze.Testing.Harness;

/// <summary>
/// One reading of every series a <see cref="MetricCollector"/> has seen.
/// Lookups match the instrument name on any listened meter and, when a tag
/// such as "outcome=failed" is given, only series carrying that tag.
/// </summary>
public sealed class MetricSnapshot
{
    public MetricSnapshot(IReadOnlyDictionary<MetricKey, MetricValue> values) => Values = values;

    public IReadOnlyDictionary<MetricKey, MetricValue> Values { get; }

    /// <summary>Counter and histogram totals, or the sum of the last gauge values.</summary>
    public double Sum(string instrument, string? tag = null)
        => Matching(instrument, tag).Sum(static value => value.Sum);

    /// <summary>The number of measurements (for a histogram, the number of recorded values).</summary>
    public long Count(string instrument, string? tag = null)
        => Matching(instrument, tag).Sum(static value => value.Count);

    public double Max(string instrument, string? tag = null)
        => Matching(instrument, tag).Select(static value => value.Max).DefaultIfEmpty(0).Max();

    /// <summary>
    /// What changed since <paramref name="baseline"/>: counts and sums of
    /// counters, up-down counters and histograms are subtracted; gauges and
    /// observed instruments keep their current value; histogram min/max stay
    /// those of the whole run.
    /// </summary>
    public MetricSnapshot Since(MetricSnapshot baseline)
    {
        var values = new Dictionary<MetricKey, MetricValue>();
        foreach (var (key, value) in Values)
        {
            values[key] = value.Kind is MetricKind.Gauge or MetricKind.Observed || !baseline.Values.TryGetValue(key, out var before)
                ? value
                : value with { Count = value.Count - before.Count, Sum = value.Sum - before.Sum };
        }

        return new MetricSnapshot(values);
    }

    private IEnumerable<MetricValue> Matching(string instrument, string? tag)
    {
        foreach (var (key, value) in Values)
        {
            if (key.Instrument == instrument && (tag is null || key.HasTag(tag)))
            {
                yield return value;
            }
        }
    }
}
