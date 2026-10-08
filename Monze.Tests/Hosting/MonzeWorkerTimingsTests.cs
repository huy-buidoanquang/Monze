using Microsoft.Extensions.Configuration;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Hosting;

public sealed class MonzeWorkerTimingsTests
{
    [Fact]
    [Req("REQ-HOST-020")]
    public void Defaults_match_the_former_hard_coded_worker_values()
    {
        var timings = MonzeWorkerTimings.From(Configuration());

        Assert.Equal(
            new MonzeWorkerTimings(
                SchedulerInterval: TimeSpan.FromSeconds(1),
                MaintenanceInterval: TimeSpan.FromMinutes(1),
                OutboxPollInterval: TimeSpan.FromMilliseconds(250),
                RoleScanInterval: TimeSpan.FromSeconds(300),
                InboxPurgeInterval: TimeSpan.FromHours(1),
                InboxRetention: TimeSpan.FromDays(30),
                MessageGapRetryBase: TimeSpan.FromMilliseconds(100),
                AgentScopeRetryBase: TimeSpan.FromMilliseconds(250),
                UncertainMarkTimeout: TimeSpan.FromSeconds(5)),
            timings);
    }

    [Theory]
    [Req("REQ-HOST-020")]
    [InlineData("1", 100)]
    [InlineData("100", 100)]
    [InlineData("750", 750)]
    [InlineData("5000", 5000)]
    [InlineData("60000", 5000)]
    public void Outbox_poll_interval_is_clamped(string configured, int expectedMilliseconds)
    {
        var timings = MonzeWorkerTimings.From(Configuration(("Monze:Outbox:PollMilliseconds", configured)));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), timings.OutboxPollInterval);
    }

    [Theory]
    [Req("REQ-HOST-020")]
    [InlineData("1", 60)]
    [InlineData("60", 60)]
    [InlineData("900", 900)]
    [InlineData("3600", 3600)]
    [InlineData("86400", 3600)]
    public void Role_scan_interval_is_clamped(string configured, int expectedSeconds)
    {
        var timings = MonzeWorkerTimings.From(Configuration(("Monze:Roles:ScanIntervalSeconds", configured)));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), timings.RoleScanInterval);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
}
