namespace Monze.Testing.Harness;

/// <summary>
/// Identifies one metric series: meter, instrument and its tags as sorted
/// "key=value" pairs joined by commas (empty when untagged).
/// </summary>
public sealed record MetricKey(string Meter, string Instrument, string Tags)
{
    public bool HasTag(string tag)
    {
        foreach (var part in Tags.Split(','))
        {
            if (string.Equals(part, tag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
