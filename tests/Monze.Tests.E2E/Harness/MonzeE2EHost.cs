using Microsoft.Extensions.DependencyInjection;
using Monze.Simulator;
using Monze.Testing;
using Monze.Testing.Postgres;
using Xunit;

// Each test starts a whole Monze host; running them one at a time keeps
// timings and logs readable.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Monze.Tests.E2E.Harness;

/// <summary>
/// A <see cref="SimulatedMonzeHost"/> on the campaign PostgreSQL
/// (MONZE_TEST_POSTGRES) with xUnit assertions for a clean run.
/// </summary>
internal sealed class MonzeE2EHost : IAsyncDisposable
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);
    private readonly SimulatedMonzeHost _host;

    private MonzeE2EHost(SimulatedMonzeHost host) => _host = host;

    public MezonSimulator Simulator => _host.Simulator;

    public SimWorld World => _host.World;

    public SimRecorder Recorder => _host.Recorder;

    public SimInbound Inbound => _host.Inbound;

    public HostLogSink Logs => _host.Logs;

    public CampaignDatabase Database => _host.Database;

    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// Creates and migrates a database, starts Monze on the simulator and
    /// waits until it joined the clans. <paramref name="configuration"/>
    /// overrides the in-memory configuration (for example secret canaries or
    /// rate-limit buckets), <paramref name="services"/> replaces registrations
    /// after production composition, and <paramref name="seed"/> writes to the
    /// migrated database before the host starts; <paramref name="timings"/>
    /// adjusts the short E2E worker timings.
    /// </summary>
    public static async Task<MonzeE2EHost> StartAsync(
        SimWorld world,
        string tag,
        MezonSimulatorOptions? simulatorOptions = null,
        Action<SimFaultPlan>? faults = null,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? services = null,
        Func<CampaignDatabase, Task>? seed = null,
        Func<MonzeWorkerTimings, MonzeWorkerTimings>? timings = null)
        => new(await SimulatedMonzeHost.StartAsync(
            world,
            TestPostgres.ConnectionString,
            $"e2e_{tag}",
            new SimulatedMonzeHostOptions
            {
                Simulator = simulatorOptions,
                Faults = faults,
                Configuration = configuration ?? new Dictionary<string, string?>(),
                Services = services,
                SeedAsync = seed,
                Timings = timings,
                StartTimeout = StartTimeout
            }));

    public Task<string> WaitForCommandStatusAsync(long clanId, long channelId, long messageId, TimeSpan timeout)
        => _host.WaitForCommandStatusAsync(clanId, channelId, messageId, timeout);

    public Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
        => _host.ScalarAsync<T>(sql, parameters);

    public Task<IReadOnlyList<object?[]>> RowsAsync(string sql, params (string Name, object Value)[] parameters)
        => _host.RowsAsync(sql, parameters);

    /// <summary>No unmodelled calls, no protocol violations, no error or exception in the host log.</summary>
    public void AssertClean(bool allowWarningsWithExceptions = false)
    {
        var problems = _host.Problems(allowWarningsWithExceptions);
        Assert.True(problems.Count == 0, $"Run was not clean:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}{Environment.NewLine}{Recorder.Describe()}");
    }

    /// <summary>
    /// Stops the host (MonzeBot disconnects, workers drain) but keeps the
    /// simulator, logs and database, so a test can assert a clean shutdown.
    /// </summary>
    public Task StopHostAsync()
    {
        IsStopped = true;
        return _host.StopHostAsync();
    }

    /// <summary>Whether <see cref="StopHostAsync"/> was called (workers are expected to have exited).</summary>
    public bool IsStopped { get; private set; }

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
