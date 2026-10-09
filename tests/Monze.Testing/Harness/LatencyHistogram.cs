namespace Monze.Testing.Harness;

/// <summary>
/// A thread-safe log-linear histogram of non-negative values (the campaign
/// records latencies in microseconds). Values below 256 are kept exactly;
/// above that every power of two is split into 128 buckets, so a reported
/// percentile is at most 1/128 (0.79 %) above the true value and never below
/// it. Recording is lock-free and allocation-free; reads taken while other
/// threads record are approximate.
/// </summary>
public sealed class LatencyHistogram
{
    private const int SubBucketBits = 7;
    private const int SubBucketCount = 1 << SubBucketBits;
    private const int ExactCount = SubBucketCount * 2;
    private const int BucketCount = ExactCount + (63 - (SubBucketBits + 1)) * SubBucketCount;

    /// <summary>The largest relative error of a reported percentile.</summary>
    public const double RelativeError = 1.0 / SubBucketCount;

    private readonly long[] _counts = new long[BucketCount];
    private long _count;
    private long _sum;
    private long _min = long.MaxValue;
    private long _max = long.MinValue;

    public long Count => Interlocked.Read(ref _count);

    public long Min => Count == 0 ? 0 : Interlocked.Read(ref _min);

    public long Max => Count == 0 ? 0 : Interlocked.Read(ref _max);

    public double Mean => Count == 0 ? 0 : (double)Interlocked.Read(ref _sum) / Count;

    public void Record(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Interlocked.Increment(ref _counts[IndexOf(value)]);
        Interlocked.Add(ref _sum, value);
        UpdateMin(value);
        UpdateMax(value);
        Interlocked.Increment(ref _count);
    }

    public void Record(TimeSpan elapsed) => Record(Math.Max(0, elapsed.Ticks / TimeSpan.TicksPerMicrosecond));

    /// <summary>Adds every value recorded in <paramref name="other"/> to this histogram.</summary>
    public void Add(LatencyHistogram other)
    {
        long added = 0;
        for (var i = 0; i < BucketCount; i++)
        {
            var count = Interlocked.Read(ref other._counts[i]);
            if (count != 0)
            {
                Interlocked.Add(ref _counts[i], count);
                added += count;
            }
        }

        if (added == 0)
        {
            return;
        }

        Interlocked.Add(ref _sum, Interlocked.Read(ref other._sum));
        UpdateMin(Interlocked.Read(ref other._min));
        UpdateMax(Interlocked.Read(ref other._max));
        Interlocked.Add(ref _count, added);
    }

    /// <summary>
    /// The smallest bucket bound at or above which no more than
    /// (100 - <paramref name="percentile"/>) % of the values lie, capped at <see cref="Max"/>.
    /// </summary>
    public long ValueAtPercentile(double percentile)
    {
        if (percentile is < 0 or > 100 || double.IsNaN(percentile))
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be within 0..100.");
        }

        var total = Count;
        if (total == 0)
        {
            return 0;
        }

        var rank = Math.Max(1, (long)Math.Ceiling(percentile / 100 * total));
        long seen = 0;
        for (var i = 0; i < BucketCount; i++)
        {
            seen += Interlocked.Read(ref _counts[i]);
            if (seen >= rank)
            {
                return Math.Min(UpperBound(i), Max);
            }
        }

        return Max;
    }

    public HistogramSummary Summarize() => new(
        Count,
        Min,
        Mean,
        ValueAtPercentile(50),
        ValueAtPercentile(90),
        ValueAtPercentile(95),
        ValueAtPercentile(99),
        ValueAtPercentile(99.9),
        Max);

    internal static int IndexOf(long value)
    {
        if (value < ExactCount)
        {
            return (int)value;
        }

        var exponent = 63 - long.LeadingZeroCount(value);
        var shift = exponent - SubBucketBits;
        var mantissa = value >> (int)shift;
        return (int)(ExactCount + (exponent - (SubBucketBits + 1)) * SubBucketCount + (mantissa - SubBucketCount));
    }

    internal static long UpperBound(int index)
    {
        if (index < ExactCount)
        {
            return index;
        }

        var octave = (index - ExactCount) / SubBucketCount;
        var mantissa = SubBucketCount + (index - ExactCount) % SubBucketCount;
        var shift = octave + 1;
        var upper = ((long)(mantissa + 1) << shift) - 1;
        return upper < 0 ? long.MaxValue : upper;
    }

    private void UpdateMin(long value)
    {
        var current = Interlocked.Read(ref _min);
        while (value < current)
        {
            var seen = Interlocked.CompareExchange(ref _min, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    private void UpdateMax(long value)
    {
        var current = Interlocked.Read(ref _max);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref _max, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
