namespace Monze.Testing;

/// <summary>
/// Redis connection for integration tests. Only a loopback campaign Redis is
/// accepted; the shared mezube-redis-1 container on 6379 is refused.
/// </summary>
public static class TestRedis
{
    public const string ConnectionVariable = "MONZE_REDIS_CONNECTION";

    public static bool IsConfigured
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable));

    public static string ConnectionString
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(ConnectionVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Set {ConnectionVariable} to a throwaway campaign Redis (see scripts/run-test-campaign.ps1).");
            }

            AssertCampaignRedis(value);
            return value;
        }
    }

    public static void AssertCampaignRedis(string configuration)
    {
        var endpoint = configuration.Split(',', 2)[0].Trim();
        var separator = endpoint.LastIndexOf(':');
        var host = separator > 0 ? endpoint[..separator] : endpoint;
        var port = separator > 0 && int.TryParse(endpoint[(separator + 1)..], out var parsed) ? parsed : 6379;
        var loopback = host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
        if (!loopback || port == 6379)
        {
            throw new InvalidOperationException("Test Redis must be a loopback campaign container (not port 6379).");
        }
    }
}
