using Mezon.Net.Core;
using Microsoft.Extensions.Configuration;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Hosting;

public sealed class MonzeClientOptionsTests
{
    [Fact]
    [Req("REQ-HOST-021")]
    public void Defaults_use_the_public_gateway_over_websocket_with_sdk_rate_limits()
    {
        var options = MonzeBot.CreateClientOptions(Configuration(), 7, "token", customization: null);

        Assert.Equal(7, options.BotId);
        Assert.Equal("gw.mezon.ai", options.Host);
        Assert.Equal("443", options.Port);
        Assert.True(options.UseSSL);
        Assert.Equal(TransportType.WebSocket, options.TransportType);
        Assert.Equal(string.Empty, options.AgentEventUrl);
        Assert.Equal(60, options.MaxTransportRequestsPerSecond);
        Assert.Equal(500, options.MaxTransportRequestsPerMinute);
        Assert.Equal(2, options.MaxConnectRequestsPerSecond);
        Assert.Null(options.SocketHandlerTimeoutInMilliseconds);
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
