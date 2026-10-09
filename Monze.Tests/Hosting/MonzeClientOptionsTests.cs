using Mezon.Net.Core;
using Microsoft.Extensions.Configuration;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Hosting;

public sealed class MonzeClientOptionsTests
{
    [Fact]
    [Req("REQ-HOST-021")]
    public void Defaults_use_the_public_gateway_over_websocket_with_an_evenly_paced_budget()
    {
        var options = MonzeBot.CreateClientOptions(Configuration(), 7, "token", customization: null);

        Assert.Equal(7, options.BotId);
        Assert.Equal("gw.mezon.ai", options.Host);
        Assert.Equal("443", options.Port);
        Assert.True(options.UseSSL);
        Assert.Equal(TransportType.WebSocket, options.TransportType);
        Assert.Equal(string.Empty, options.AgentEventUrl);
        Assert.Equal(8, options.MaxTransportRequestsPerSecond);
        Assert.Equal(500, options.MaxTransportRequestsPerMinute);
        Assert.Equal(2, options.MaxConnectRequestsPerSecond);
        Assert.Null(options.SocketHandlerTimeoutInMilliseconds);
        Assert.Equal(
            nameof(MonzeMetrics.RecordUpstreamRateLimit),
            Assert.IsType<Func<IRateLimitInfo, Task>>(options.DefaultRatelimitCallback).Method.Name);
    }

    [Fact]
    [Req("REQ-HOST-021")]
    public void Configured_rate_limits_are_clamped_and_tcp_is_selected_by_name()
    {
        var options = MonzeBot.CreateClientOptions(
            Configuration(
                ("Mezon:Transport", "tcp"),
                ("Mezon:RateLimit:RequestsPerSecond", "5000"),
                ("Mezon:RateLimit:RequestsPerMinute", "0"),
                ("Mezon:RateLimit:ConnectRequestsPerSecond", "1000")),
            7,
            "token",
            customization: null);

        Assert.Equal(TransportType.Tcp, options.TransportType);
        Assert.Equal(1000, options.MaxTransportRequestsPerSecond);
        Assert.Equal(1, options.MaxTransportRequestsPerMinute);
        Assert.Equal(100, options.MaxConnectRequestsPerSecond);
    }

    /// <summary>
    /// Regression for DEF-03: the per-second cap follows the minute budget
    /// (a sixtieth of it) unless configured, so the minute window is never
    /// spent in a few seconds.
    /// </summary>
    [Theory]
    [Req("REQ-HOST-021")]
    [InlineData("900", null, 15)]
    [InlineData("30", null, 1)]
    [InlineData("500", "60", 60)]
    public void The_per_second_cap_defaults_to_a_sixtieth_of_the_minute_budget(string perMinute, string? perSecond, int expected)
    {
        var values = new List<(string Key, string Value)> { ("Mezon:RateLimit:RequestsPerMinute", perMinute) };
        if (perSecond is not null)
        {
            values.Add(("Mezon:RateLimit:RequestsPerSecond", perSecond));
        }

        var options = MonzeBot.CreateClientOptions(Configuration([.. values]), 7, "token", customization: null);

        Assert.Equal(expected, options.MaxTransportRequestsPerSecond);
    }

    /// <summary>
    /// Regression for DEF-03: bulk outbox delivery gets a share of the minute
    /// budget (half by default) with about two seconds of burst, and follows
    /// a customized budget (the simulator's unthrottled one).
    /// </summary>
    [Theory]
    [Req("REQ-HOST-021", "REQ-OUT-001")]
    [InlineData(null, 600, 10, 110)]
    [InlineData("25", 600, 5, 55)]
    [InlineData("200", 600, 20, 220)]
    public void The_outbox_paces_itself_to_its_share_of_the_minute_budget(string? share, int perMinute, int burst, int withinTwentySeconds)
    {
        var configuration = share is null ? Configuration() : Configuration(("Monze:Outbox:TransportSharePercent", share));
        var options = MonzeBot.CreateClientOptions(configuration, 7, "token", new MonzeClientCustomization(options => options.MaxTransportRequestsPerMinute = perMinute));

        var pacer = MonzeBot.CreateOutboxPacer(configuration, options, new ManualTimeProvider(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(burst, pacer.Available(TimeSpan.Zero));
        Assert.Equal(withinTwentySeconds, pacer.Available(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    [Req("REQ-HOST-022")]
    public void Customization_runs_after_configuration_and_can_override_it()
    {
        var seen = 0;
        var customization = new MonzeClientCustomization(options =>
        {
            seen = options.MaxTransportRequestsPerMinute;
            options.MaxTransportRequestsPerMinute = 1_000_000;
            options.Host = "127.0.0.1";
        });

        var options = MonzeBot.CreateClientOptions(
            Configuration(("Mezon:RateLimit:RequestsPerMinute", "900")),
            7,
            "token",
            customization);

        Assert.Equal(900, seen);
        Assert.Equal(1_000_000, options.MaxTransportRequestsPerMinute);
        Assert.Equal("127.0.0.1", options.Host);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
}
