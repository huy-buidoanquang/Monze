using Monze.Testing;
using Xunit;

namespace Monze.Tests.Hosting;

/// <summary>
/// Regression for DEF-03: sends are paced at a steady rate with a small
/// burst, never as a minute's budget in a few seconds followed by silence.
/// </summary>
public sealed class UpstreamPacerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    [Req("REQ-HOST-021")]
    public void A_burst_is_free_then_each_send_waits_its_own_slot()
    {
        var time = new ManualTimeProvider(Start);
        var pacer = new UpstreamPacer(perMinute: 60, burst: 3, time);

        Assert.Equal(TimeSpan.Zero, pacer.Reserve());
        Assert.Equal(TimeSpan.Zero, pacer.Reserve());
        Assert.Equal(TimeSpan.Zero, pacer.Reserve());
        Assert.Equal(TimeSpan.FromSeconds(1), pacer.Reserve());
        Assert.Equal(TimeSpan.FromSeconds(2), pacer.Reserve());

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(1), pacer.Reserve());
    }

    [Fact]
    [Req("REQ-HOST-021")]
    public void An_idle_pacer_banks_at_most_its_burst()
    {
        var time = new ManualTimeProvider(Start);
        var pacer = new UpstreamPacer(perMinute: 120, burst: 4, time);
        time.Advance(TimeSpan.FromHours(1));

        var free = 0;
        while (pacer.Reserve() == TimeSpan.Zero)
        {
            free++;
        }

        Assert.Equal(4, free);
    }

    [Fact]
    [Req("REQ-HOST-021")]
    public void Available_counts_the_unreserved_slots_within_the_horizon()
    {
        var time = new ManualTimeProvider(Start);
        var pacer = new UpstreamPacer(perMinute: 300, burst: 10, time);

        Assert.Equal(10, pacer.Available(TimeSpan.Zero));
        Assert.Equal(15, pacer.Available(TimeSpan.FromSeconds(1)));
        Assert.Equal(110, pacer.Available(TimeSpan.FromSeconds(20)));

        for (var i = 0; i < 110; i++)
        {
            pacer.Reserve();
        }

        Assert.Equal(0, pacer.Available(TimeSpan.FromSeconds(20)));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(5, pacer.Available(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    [Req("REQ-HOST-021")]
    public void A_rate_of_zero_disables_pacing()
    {
        var pacer = new UpstreamPacer(perMinute: 0, burst: 1, new ManualTimeProvider(Start));

        for (var i = 0; i < 1_000; i++)
        {
            Assert.Equal(TimeSpan.Zero, pacer.Reserve());
        }

        Assert.Equal(int.MaxValue, pacer.Available(TimeSpan.Zero));
    }
}
