using System.Collections.Concurrent;
using System.Net;
using Npgsql;

namespace Monze.Testing;

/// <summary>
/// The only source of PostgreSQL connection strings for tests. It reads
/// MONZE_TEST_POSTGRES and refuses anything that is not a throwaway campaign
/// database, so no test can reach the development database configured in
/// appsettings.json.
/// </summary>
public static class TestPostgres
{
    public const string ConnectionVariable = "MONZE_TEST_POSTGRES";

    /// <summary>A second campaign server (another PostgreSQL major version) for the migration matrix.</summary>
    public const string AlternateConnectionVariable = "MONZE_TEST_POSTGRES_ALT";

    public const string ClusterNamePrefix = "monze-test-";
    public const string DatabaseNamePrefix = "monze_";

    private static readonly ConcurrentDictionary<string, Lazy<string?>> VerifiedServers = new(StringComparer.Ordinal);

    public static bool IsConfigured
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable));

    public static bool IsAlternateConfigured
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AlternateConnectionVariable));

    /// <summary>
    /// Returns the guarded connection string, or throws when it is missing or
    /// points anywhere other than a local campaign container.
    /// </summary>
    public static string ConnectionString => Read(ConnectionVariable);

    /// <summary>The guarded connection string of the second campaign server.</summary>
    public static string AlternateConnectionString => Read(AlternateConnectionVariable);

    private static string Read(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Set {variable} to a throwaway campaign PostgreSQL (see scripts/run-test-campaign.ps1).");
        }

        AssertCampaignDatabase(value);
        return value;
    }

    /// <summary>
    /// Validates the connection string shape and, once per server, that the
    /// server is a campaign container (cluster_name starts with monze-test-).
    /// </summary>
    public static void AssertCampaignDatabase(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!IsLoopback(builder.Host))
        {
            throw new InvalidOperationException("Test PostgreSQL must be a loopback host.");
        }

        if (builder.Port == 5432)
        {
            throw new InvalidOperationException("Test PostgreSQL must not use port 5432 (reserved for the local service).");
        }

        if (string.IsNullOrWhiteSpace(builder.Database)
            || !builder.Database.StartsWith(DatabaseNamePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Test database name must start with {DatabaseNamePrefix}.");
        }

        var serverKey = $"{builder.Host}:{builder.Port}";
        var failure = VerifiedServers.GetOrAdd(
            serverKey,
            _ => new Lazy<string?>(() => VerifyCluster(builder))).Value;
        if (failure is not null)
        {
            throw new InvalidOperationException(failure);
        }
    }

    private static string? VerifyCluster(NpgsqlConnectionStringBuilder builder)
    {
        try
        {
            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            using var command = new NpgsqlCommand("SHOW cluster_name;", connection);
            var cluster = command.ExecuteScalar() as string;
            return cluster is not null && cluster.StartsWith(ClusterNamePrefix, StringComparison.Ordinal)
                ? null
                : "Test PostgreSQL is not a campaign container (cluster_name mismatch).";
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return $"Test PostgreSQL is unreachable ({ex.GetType().Name}).";
        }
    }

    private static bool IsLoopback(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
