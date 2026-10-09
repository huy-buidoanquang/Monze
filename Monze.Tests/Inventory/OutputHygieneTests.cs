using Monze.Testing;
using Xunit;

namespace Monze.Tests.Inventory;

public sealed class OutputHygieneTests
{
    [Fact]
    [Req("REQ-SEC-100")]
    public void Test_output_does_not_carry_application_settings()
    {
        var files = Directory.GetFiles(AppContext.BaseDirectory, "appsettings*.json");

        Assert.Empty(files);
    }

    [Theory]
    [Req("REQ-SEC-101")]
    [InlineData("Host=db.example.com;Port=55432;Database=monze_t_x;Username=u;Password=p")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=monze_t_x;Username=u;Password=p")]
    [InlineData("Host=127.0.0.1;Port=55432;Database=postgres;Username=u;Password=p")]
    [InlineData("Host=10.0.0.5;Port=55432;Database=monze_t_x;Username=u;Password=p")]
    public void Test_database_guard_refuses_non_campaign_targets(string connectionString)
    {
        Assert.Throws<InvalidOperationException>(() => TestPostgres.AssertCampaignDatabase(connectionString));
    }

    [Theory]
    [Req("REQ-SEC-101")]
    [InlineData("127.0.0.1:6379")]
    [InlineData("redis.example.com:56379")]
    public void Test_redis_guard_refuses_shared_or_remote_targets(string configuration)
    {
        Assert.Throws<InvalidOperationException>(() => TestRedis.AssertCampaignRedis(configuration));
    }
}
