using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monze.Hosting;
using Monze.Infrastructure.Persistence;
using Monze.Testing.Postgres;
using Npgsql;

namespace Monze.Simulator;

/// <summary>
/// The real Monze host (MonzeBot, MonzeApp, PostgreSQL repositories, workers)
/// running against a throwaway campaign database and the offline
/// <see cref="MezonSimulator"/>. Configuration is in memory only (no
/// appsettings), worker intervals are short by default, and start-up returns
/// once the bot logged in, discovered the seeded clans and joined them.
/// Disposing stops the host, closes the simulator and drops the database.
/// E2E tests, load runs and chaos scenarios share it.
/// </summary>
public sealed class SimulatedMonzeHost : IAsyncDisposable
{
    private const string RefreshCompleted = "Monze clan registry refresh completed";
    private readonly IHost _host;
    private readonly DirectoryInfo _root;
    private int _hostStopped;
    private bool _disposed;

    private SimulatedMonzeHost(IHost host, MezonSimulator simulator, HostLogSink logs, CampaignDatabase database, DirectoryInfo root)
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

    /// <summary>
    /// Creates and migrates a database on the guarded campaign
    /// <paramref name="server"/>, starts Monze on the simulator and waits
    /// until it joined the clans.
    /// </summary>
    public static async Task<SimulatedMonzeHost> StartAsync(SimWorld world, string server, string tag, SimulatedMonzeHostOptions? options = null)
    {
        options ??= new SimulatedMonzeHostOptions();
        var database = await CampaignDatabase.CreateEmptyAsync(server, tag);
        var root = Directory.CreateTempSubdirectory("monze-sim-host-");
        IHost? host = null;
        var simulator = new MezonSimulator(world, options.Simulator);
        try
        {
            await using (var dataSource = NpgsqlDataSource.Create(database.ConnectionString))
            {
                await PostgresMigrator.ApplyAsync(dataSource, database.ConnectionString, CancellationToken.None);
            }

            if (options.SeedAsync is { } seed)
            {
                await seed(database);
            }

            options.Faults?.Invoke(simulator.Faults);
            var logs = options.Logs ?? new HostLogSink();
            var configuration = Configuration(world, simulator, database, root);
            foreach (var (key, value) in options.Configuration)
            {
                configuration[key] = value;
            }

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
            if (options.Timings is { } adjust)
            {
                timings = adjust(timings);
            }

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
                    options.Services?.Invoke(services);
                }
            });
            host = builder.Build();
            await host.StartAsync();
            var started = new SimulatedMonzeHost(host, simulator, logs, database, root);
            await started.WaitUntilReadyAsync(options.StartTimeout);
            return started;
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

    /// <summary>
    /// Unmodelled calls, protocol violations and host log problems (errors,
    /// and unless allowed, warnings carrying an exception); empty when clean.
    /// </summary>
    public IReadOnlyList<string> Problems(bool allowWarningsWithExceptions = false)
    {
        var problems = new List<string>();
        problems.AddRange(Recorder.UnmodelledCalls.Select(static call => $"unmodelled: {call}"));
        problems.AddRange(Recorder.ProtocolViolations.Select(static violation => $"protocol: {violation}"));
        problems.AddRange(Logs.Problems
            .Where(entry => entry.Level >= LogLevel.Error || !allowWarningsWithExceptions)
            .Select(static entry => $"log: {entry}"));
        return problems;
    }

    /// <summary>
    /// Stops the host (MonzeBot disconnects, workers drain) but keeps the
    /// simulator, logs and database, so a caller can check a clean shutdown.
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

    private async Task WaitUntilReadyAsync(TimeSpan startTimeout)
    {
        using var timeout = new CancellationTokenSource(startTimeout);
        var failure = Logs.WaitForAsync(static entry => entry.Level >= LogLevel.Critical || entry.Category == "Microsoft.Extensions.Hosting.Internal.Host" && entry.Level >= LogLevel.Error, startTimeout, timeout.Token);
        var ready = Logs.WaitForAsync(static entry => entry.Message.StartsWith(RefreshCompleted, StringComparison.Ordinal), startTimeout, timeout.Token);
        if (await Task.WhenAny(ready, failure) == failure && failure.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException($"Monze failed to start: {failure.Result}{Environment.NewLine}{Logs.Describe()}");
        }

        await ready;
        await timeout.CancelAsync();
        foreach (var clan in World.ClansOf(World.Bot.Id))
        {
            if (!Simulator.ConnectedSessions.Any(session => session.HasJoinedClan(clan.Id)))
            {
                throw new InvalidOperationException($"Monze is ready but no session joined clan {clan.Id}.{Environment.NewLine}{Recorder.Describe()}");
            }
        }
    }

    private static Dictionary<string, string?> Configuration(
        SimWorld world,
        MezonSimulator simulator,
        CampaignDatabase database,
        DirectoryInfo root)
        => new()
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
