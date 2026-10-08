namespace Monze.Campaign.Soak;

/// <summary>
/// Trend tests for soak samples: the Mann–Kendall test for a monotonic
/// increase (one-sided p-value, no tie correction) and Sen's slope (the
/// median of all pairwise slopes, robust to outliers). A leak criterion fails
/// only when the increase is significant AND the slope exceeds its budget.
/// </summary>
public static class TrendAnalysis
{
    /// <summary>One-sided p-value that <paramref name="values"/> increase over time.</summary>
    public static double MannKendallIncreasingP(IReadOnlyList<double> values)
    {
        var n = values.Count;
        if (n < 4)
        {
            return 1;
        }

        long s = 0;
        for (var i = 0; i < n - 1; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                s += Math.Sign(values[j] - values[i]);
            }
        }

        var variance = n * (n - 1.0) * (2 * n + 5) / 18;
        var z = s > 0 ? (s - 1) / Math.Sqrt(variance) : s < 0 ? (s + 1) / Math.Sqrt(variance) : 0;
        return 1 - NormalCdf(z);
    }

    /// <summary>Sen's slope in value units per hour, for samples taken at <paramref name="hours"/>.</summary>
    public static double SenSlopePerHour(IReadOnlyList<double> hours, IReadOnlyList<double> values)
    {
        var slopes = new List<double>();
        for (var i = 0; i < values.Count - 1; i++)
        {
            for (var j = i + 1; j < values.Count; j++)
            {
                if (hours[j] > hours[i])
                {
                    slopes.Add((values[j] - values[i]) / (hours[j] - hours[i]));
                }
            }
        }

        if (slopes.Count == 0)
        {
            return 0;
        }

        slopes.Sort();
        var middle = slopes.Count / 2;
        return slopes.Count % 2 == 1 ? slopes[middle] : (slopes[middle - 1] + slopes[middle]) / 2;
    }

    // Abramowitz–Stegun 7.1.26 approximation of erf, accurate to about 1.5e-7.
    private static double NormalCdf(double z)
    {
        var x = Math.Abs(z) / Math.Sqrt(2);
        var t = 1 / (1 + 0.3275911 * x);
        var erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return z >= 0 ? (1 + erf) / 2 : (1 - erf) / 2;
    }
}
