using Monze.Testing;
using Monze.Testing.Harness;
using Xunit;

namespace Monze.Tests.Harness;

public sealed class LatencyHistogramTests
{
    private static readonly double[] Percentiles = [0, 1, 25, 50, 75, 90, 95, 99, 99.9, 99.99, 100];

    [Fact]
    [Req("REQ-HARN-001")]
    public void Small_values_are_exact()
    {
        var histogram = new LatencyHistogram();
        for (var value = 0; value < 256; value++)
        {
            histogram.Record(value);
        }

        Assert.Equal(256, histogram.Count);
        Assert.Equal(0, histogram.Min);
        Assert.Equal(255, histogram.Max);
        Assert.Equal(127.5, histogram.Mean);
        Assert.Equal(127, histogram.ValueAtPercentile(50));
        Assert.Equal(0, histogram.ValueAtPercentile(0));
        Assert.Equal(255, histogram.ValueAtPercentile(100));
    }

    [Theory]
    [Req("REQ-HARN-001")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Percentiles_are_never_low_and_at_most_one_bucket_high(int seed)
    {
        var random = new Random(seed);
        var histogram = new LatencyHistogram();
        var values = new long[50_000];
        for (var i = 0; i < values.Length; i++)
        {
            // Log-uniform from 1 µs to about 1,000 s, plus a few huge outliers.
            values[i] = i % 5_000 == 0 ? long.MaxValue / (i + 2) : (long)Math.Exp(random.NextDouble() * Math.Log(1e9));
            histogram.Record(values[i]);
        }

        Array.Sort(values);
        foreach (var percentile in Percentiles)
        {
            var rank = Math.Max(1, (long)Math.Ceiling(percentile / 100 * values.Length));
            var exact = values[rank - 1];
            var reported = histogram.ValueAtPercentile(percentile);
            Assert.True(reported >= exact, $"p{percentile}: {reported} < exact {exact}");
            Assert.True(reported <= exact + exact * LatencyHistogram.RelativeError, $"p{percentile}: {reported} too far above exact {exact}");
        }

        Assert.Equal(values[0], histogram.Min);
        Assert.Equal(values[^1], histogram.Max);
    }

    [Fact]
    [Req("REQ-HARN-001")]
    public async Task Concurrent_recording_loses_nothing_and_merging_adds_up()
    {
        var histogram = new LatencyHistogram();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 1; i <= 100_000; i++)
            {
                histogram.Record(i * (worker + 1));
            }
        })));

        Assert.Equal(800_000, histogram.Count);
        Assert.Equal(1, histogram.Min);
        Assert.Equal(800_000, histogram.Max);
        Assert.Equal(50_000.5 * 4.5, histogram.Mean, 6);

        var other = new LatencyHistogram();
        other.Record(TimeSpan.FromSeconds(2));
        histogram.Add(other);
        histogram.Add(new LatencyHistogram());
        Assert.Equal(800_001, histogram.Count);
        Assert.Equal(2_000_000, histogram.Max);
        Assert.Equal(2_000_000, histogram.ValueAtPercentile(100));
    }

    [Fact]
    [Req("REQ-HARN-001")]
    public void Empty_and_invalid_input()
    {
        var histogram = new LatencyHistogram();
        Assert.Equal(new HistogramSummary(0, 0, 0, 0, 0, 0, 0, 0, 0), histogram.Summarize());
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.Record(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.ValueAtPercentile(100.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.ValueAtPercentile(double.NaN));
    }
}
