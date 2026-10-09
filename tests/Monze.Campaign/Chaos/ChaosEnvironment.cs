using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Monze.Testing.Harness;
using Npgsql;
using StackExchange.Redis;

namespace Monze.Campaign.Chaos;

/// <summary>
/// The dependencies chaos scenarios break: a dedicated, durable PostgreSQL
/// 17 container (fsync on, so SIGKILL is survivable) and a Redis container,
/// both labelled with the campaign id and published on free loopback ports,
/// each behind a <see cref="TcpFaultProxy"/>. Monze connects through the
/// proxies; the harness creates databases and checks invariants directly.
/// Passwords are generated per run, handed to docker by environment name and
/// never written anywhere. Disposing removes both containers.
/// </summary>
public sealed class ChaosEnvironment : IAsyncDisposable
{
    private const string Database = "monze_t_chaos";
    private readonly string _postgresPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly string _redisPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private ChaosEnvironment(DockerControl docker, string campaignId)
    {
        Docker = docker;
        PostgresContainer = $"monze-campaign-{campaignId}-chaos-pg";
        RedisContainer = $"monze-campaign-{campaignId}-chaos-redis";
        PostgresPort = FreePort();
        RedisPort = FreePort();
    }

    public DockerControl Docker { get; }

    public string PostgresContainer { get; }

    public string RedisContainer { get; }

    public int PostgresPort { get; }

    public int RedisPort { get; }

    public TcpFaultProxy PostgresProxy { get; private set; } = null!;

    public TcpFaultProxy RedisProxy { get; private set; } = null!;

    /// <summary>The direct (unproxied) server the harness creates databases on.</summary>
    public string Server => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1",
        Port = PostgresPort,
        Database = Database,
        Username = "monze",
        Password = _postgresPassword,
        Pooling = true
    }.ConnectionString;

    /// <summary>The Redis configuration Monze uses (through the proxy).</summary>
    public string ProxiedRedis => $"127.0.0.1:{RedisProxy.Endpoint.Port},password={_redisPassword},abortConnect=false";

    /// <summary>The direct Redis configuration for harness-side faults such as FLUSHALL.</summary>
    public string DirectRedis => $"127.0.0.1:{RedisPort},password={_redisPassword},allowAdmin=true";

    public static async Task<ChaosEnvironment> StartAsync(string campaignId)
    {
        var environment = new ChaosEnvironment(new DockerControl(campaignId), campaignId);
        try
        {
            await environment.StartContainersAsync();
            environment.PostgresProxy = TcpFaultProxy.Start(new IPEndPoint(IPAddress.Loopback, environment.PostgresPort));
            environment.RedisProxy = TcpFaultProxy.Start(new IPEndPoint(IPAddress.Loopback, environment.RedisPort));
            return environment;
        }
        catch
        {
            await environment.DisposeAsync();
            throw;
        }
    }

    /// <summary>Monze's connection string for <paramref name="databaseConnectionString"/>, through the PostgreSQL proxy.</summary>
    public string Proxied(string databaseConnectionString)
        => new NpgsqlConnectionStringBuilder(databaseConnectionString)
        {
            Host = PostgresProxy.Endpoint.Address.ToString(),
            Port = PostgresProxy.Endpoint.Port
        }.ConnectionString;

    /// <summary>Restores both proxies to pass-through and both containers to running.</summary>
    public async Task HealAsync()
    {
        PostgresProxy.Mode = TcpFaultMode.Pass;
        PostgresProxy.Latency = TimeSpan.Zero;
        RedisProxy.Mode = TcpFaultMode.Pass;
        RedisProxy.Latency = TimeSpan.Zero;
        foreach (var (container, ready) in new[] { (PostgresContainer, PostgresReady), (RedisContainer, RedisReady) })
        {
            var status = await Docker.StatusAsync(container);
            if (status == "paused")
            {
                await Docker.UnpauseAsync(container);
            }
            else if (status != "running")
            {
                await Docker.StartAsync(container);
            }

            await Docker.WaitUntilReadyAsync(container, ready, TimeSpan.FromSeconds(60));
        }
    }

    public async Task FlushRedisAsync()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(DirectRedis);
        await redis.GetServer(redis.GetEndPoints()[0]).FlushAllDatabasesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (PostgresProxy is not null)
        {
            await PostgresProxy.DisposeAsync();
        }

        if (RedisProxy is not null)
        {
            await RedisProxy.DisposeAsync();
        }

        foreach (var container in new[] { PostgresContainer, RedisContainer })
        {
            try
            {
                await Docker.RemoveAsync(container);
            }
            catch (InvalidOperationException)
            {
                // Not created (or already gone); the orchestrator also removes leftovers by label.
            }
        }
    }

    private string[] PostgresReady => ["pg_isready", "-h", "127.0.0.1", "-U", "monze", "-d", Database];

    // Any reply (even NOAUTH) proves the server is up, and keeps the password off the command line.
    private static string[] RedisReady => ["redis-cli", "ping"];

    private async Task StartContainersAsync()
    {
        await Docker.RunAsync(new DockerContainerSpec(PostgresContainer, "postgres:17-alpine")
        {
            Ports = [(PostgresPort, 5432)],
            Environment = new Dictionary<string, string>
            {
                ["POSTGRES_USER"] = "monze",
                ["POSTGRES_PASSWORD"] = _postgresPassword,
                ["POSTGRES_DB"] = Database
            },
            Command = ["-c", $"cluster_name=monze-test-{PostgresContainer}", "-c", "max_connections=300"]
        });
        await Docker.RunAsync(new DockerContainerSpec(RedisContainer, "redis:7-alpine")
        {
            Ports = [(RedisPort, 6379)],
            Environment = new Dictionary<string, string> { ["REDIS_PASSWORD"] = _redisPassword },
            Command = ["sh", "-c", "exec redis-server --save '' --appendonly no --requirepass \"$REDIS_PASSWORD\""]
        });
        await Docker.WaitUntilReadyAsync(PostgresContainer, PostgresReady, TimeSpan.FromSeconds(90));
        await Docker.WaitUntilReadyAsync(RedisContainer, RedisReady, TimeSpan.FromSeconds(60));
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
