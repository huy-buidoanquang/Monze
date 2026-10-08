using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monze.Hosting;
using Monze.Infrastructure.Persistence;
using Monze.Simulator;
using Monze.Testing;
using Monze.Testing.Postgres;
using Npgsql;
using Xunit;

// Each test starts a whole Monze host; running them one at a time keeps
// timings and logs readable.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Monze.Tests.E2E.Harness;

/// <summary>
/// The real Monze host (MonzeBot, MonzeApp, PostgreSQL repositories, workers)
/// running against a throwaway campaign database and the offline
/// <see cref="MezonSimulator"/>. Configuration is in memory only (no
/// appsettings), worker intervals are short, and start-up returns once the
/// bot logged in, discovered the seeded clans and joined them. Disposing
/// stops the host, closes the simulator and drops the database.
/// </summary>
internal sealed class MonzeE2EHost : IAsyncDisposable
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);
    private const string RefreshCompleted = "Monze clan registry refresh completed";
    private readonly IHost _host;
    private readonly DirectoryInfo _root;
    private int _hostStopped;
    private bool _disposed;

    private MonzeE2EHost(IHost host, MezonSimulator simulator, HostLogSink logs, CampaignDatabase database, DirectoryInfo root)
    {
        _host = host;
        Simulator = simulator;
        Logs = logs;
        Database = database;
        _root = root;
    }

    public MezonSimulator Simulator { get; }

    public SimWorld World => Simulator.World;

    public SimRecorder Recorder => Simulator.Recorder;

    public SimInbound Inbound => Simulator.Inbound;

    public HostLogSink Logs { get; }

    public CampaignDatabase Database { get; }

    public IServiceProvider Services => _host.Services;

    /// <summary>Creates and migrates a database, starts Monze on the simulator and waits until it joined the clans.</summary>
    public static async Task<MonzeE2EHost> StartAsync(
        SimWorld world,
        string tag,
        MezonSimulatorOptions? simulatorOptions = null,
        Action<SimFaultPlan>? faults = null)
    {
        var database = await CampaignDatabase.CreateEmptyAsync(TestPostgres.ConnectionString, $"e2e_{tag}");
        var root = Directory.CreateTempSubdirectory("monze-e2e-");
        IHost? host = null;
        var simulator = new MezonSimulator(world, simulatorOptions);
        try
        {
            await using (var dataSource = NpgsqlDataSource.Create(database.ConnectionString))
            {
                await PostgresMigrator.ApplyAsync(dataSource, database.ConnectionString, CancellationToken.None);
            }

            faults?.Invoke(simulator.Faults);
            var logs = new HostLogSink();
            var configuration = Configuration(world, simulator, database, root);
            var timings = MonzeWorkerTimings.From(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build()) with
            {
                SchedulerInterval = TimeSpan.FromMilliseconds(200),
                MaintenanceInterval = TimeSpan.FromSeconds(1),
                OutboxPollInterval = TimeSpan.FromMilliseconds(100),
                RoleScanInterval = TimeSpan.FromSeconds(1),
                MessageGapRetryBase = TimeSpan.FromMilliseconds(20),
                AgentScopeRetryBase = TimeSpan.FromMilliseconds(20),
                UncertainMarkTimeout = TimeSpan.FromSeconds(2)
            };
            var builder = MonzeHostComposition.CreateBuilder([], new MonzeHostCompositionOptions
            {
                Settings = new HostApplicationBuilderSettings
                {
                    DisableDefaults = true,
                    ApplicationName = "Monze",
                    EnvironmentName = Environments.Production,
                    ContentRootPath = root.FullName
                },
                LoadAppSettingsFiles = false,
                Configuration = configuration,
                AddProductionLogging = false,
                ConfigureTestServices = services =>
                {
                    services.AddSingleton(simulator.Customization);
                    services.AddSingleton<ILoggerProvider>(logs);
                    services.AddSingleton(timings);
                    services.AddSingleton(new MonzeConnectionRetryOptions(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2)));
                }
            });
            host = builder.Build();
            await host.StartAsync();
            var e2e = new MonzeE2EHost(host, simulator, logs, database, root);
            await e2e.WaitUntilReadyAsync();
            return e2e;
        }
        catch
        {
            await StopAsync(host);
            await simulator.DisposeAsync();
            await database.DisposeAsync();
            DeleteQuietly(root);
            throw;
        }
    }

    /// <summary>Polls command_inbox until the command left 'processing'; returns its status.</summary>
    public async Task<string> WaitForCommandStatusAsync(long clanId, long channelId, long messageId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var status = await ScalarAsync<string>(
                "SELECT status FROM command_inbox WHERE clan_id = @clan AND channel_id = @channel AND message_id = @message;",
                ("clan", clanId),
                ("channel", channelId),
                ("message", messageId));
            if (status is not null && status != "processing")
            {
                return status;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Command {messageId} is still '{status ?? "missing"}' after {timeout.TotalSeconds:0.#}s.{Environment.NewLine}{Recorder.Describe()}{Environment.NewLine}{Logs.Describe()}");
            }

            await Task.Delay(50);
        }
    }

    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public async Task<IReadOnlyList<object?[]>> RowsAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>No unmodelled calls, no protocol violations, no error or exception in the host log.</summary>
    public void AssertClean(bool allowWarningsWithExceptions = false)
    {
        Assert.True(Recorder.UnmodelledCalls.Count == 0, $"Unmodelled calls:{Environment.NewLine}{Recorder.Describe()}");
        Assert.True(Recorder.ProtocolViolations.Count == 0, $"Protocol violations:{Environment.NewLine}{Recorder.Describe()}");
        var problems = Logs.Problems
            .Where(entry => entry.Level >= LogLevel.Error || !allowWarningsWithExceptions)
            .ToList();
        Assert.True(problems.Count == 0, $"Host logged problems:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    /// <summary>
    /// Stops the host (MezonBot disconnects, workers drain) but keeps the
    /// simulator, logs and database, so a test can assert a clean shutdown.
    /// </summary>
    public async Task StopHostAsync()
    {
        if (Interlocked.Exchange(ref _hostStopped, 1) == 0)
        {
            await StopAsync(_host);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopHostAsync();
        await Simulator.DisposeAsync();
        await Database.DisposeAsync();
        DeleteQuietly(_root);
    }

    private async Task WaitUntilReadyAsync()
    {
        using var timeout = new CancellationTokenSource(StartTimeout);
        var failure = Logs.WaitForAsync(static entry => entry.Level >= LogLevel.Critical || entry.Category == "Microsoft.Extensions.Hosting.Internal.Host" && entry.Level >= LogLevel.Error, StartTimeout, timeout.Token);
        var ready = Logs.WaitForAsync(static entry => entry.Message.StartsWith(RefreshCompleted, StringComparison.Ordinal), StartTimeout, timeout.Token);
        if (await Task.WhenAny(ready, failure) == failure && failure.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException($"Monze failed to start: {failure.Result}{Environment.NewLine}{Logs.Describe()}");
        }

        await ready;
        await timeout.CancelAsync();
        foreach (var clan in World.ClansOf(World.Bot.Id))
        {
            Assert.True(
                Simulator.ConnectedSessions.Any(session => session.HasJoinedClan(clan.Id)),
                $"Monze is ready but no session joined clan {clan.Id}.{Environment.NewLine}{Recorder.Describe()}");
        }
    }

    private static IEnumerable<KeyValuePair<string, string?>> Configuration(
        SimWorld world,
        MezonSimulator simulator,
        CampaignDatabase database,
        DirectoryInfo root)
        => new Dictionary<string, string?>
        {
            ["Monze:Postgres"] = database.ConnectionString,
            ["Monze:EnvironmentName"] = "e2e",
            ["Monze:Logging:Directory"] = Path.Combine(root.FullName, "logs"),
            ["Monze:SqliteDirectory"] = Path.Combine(root.FullName, "data"),
            ["Mezon:BotId"] = world.Bot.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Mezon:Token"] = world.Bot.Token,
            ["Mezon:Host"] = simulator.Options.Host,
            ["Mezon:Port"] = simulator.Options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Mezon:UseSsl"] = "false",
            ["Mezon:AgentBaseUrl"] = string.Empty
        };

    private static async Task StopAsync(IHost? host)
    {
        if (host is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await host.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }

        if (host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            host.Dispose();
        }
    }

    private static void DeleteQuietly(DirectoryInfo directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (directory.Exists)
                {
                    directory.Delete(recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }

            directory.Refresh();
        }
    }
}
