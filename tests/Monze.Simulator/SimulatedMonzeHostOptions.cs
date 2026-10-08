using Microsoft.Extensions.DependencyInjection;
using Monze.Hosting;
using Monze.Testing.Postgres;

namespace Monze.Simulator;

/// <summary>
/// How a <see cref="SimulatedMonzeHost"/> starts: simulator options and
/// faults, worker timings (the defaults are the short E2E timings),
/// configuration entries that override the in-memory defaults, extra
/// services, the log sink, seeding of the migrated database before the host
/// starts, and how long to wait for the bot to join its clans.
/// </summary>
public sealed record SimulatedMonzeHostOptions
{
    public MezonSimulatorOptions? Simulator { get; init; }

    public Action<SimFaultPlan>? Faults { get; init; }

    public Func<MonzeWorkerTimings, MonzeWorkerTimings>? Timings { get; init; }

    public IReadOnlyDictionary<string, string?> Configuration { get; init; } = new Dictionary<string, string?>();

    public Action<IServiceCollection>? Services { get; init; }

    public HostLogSink? Logs { get; init; }

    public Func<CampaignDatabase, Task>? SeedAsync { get; init; }

    /// <summary>Runs on the new simulator before the host starts, e.g. to observe its recorder from the first action.</summary>
    public Action<MezonSimulator>? Prepare { get; init; }

    /// <summary>
    /// Maps the database's connection string to the one Monze uses, e.g. to
    /// route Monze through a fault proxy while the harness talks to the
    /// server directly.
    /// </summary>
    public Func<string, string>? MonzeConnectionString { get; init; }

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// An existing database to run on (a restart, or a second instance next
    /// to a running one). The host neither creates, migrates nor drops it;
    /// <see cref="SeedAsync"/> still runs when set.
    /// </summary>
    public CampaignDatabase? Database { get; init; }

    /// <summary>
    /// An existing simulator to connect to (same world, bot token, recorder
    /// and fault plan), e.g. for a restart or a second instance. The host does
    /// not dispose it; <see cref="Simulator"/> options are ignored.
    /// </summary>
    public MezonSimulator? SharedSimulator { get; init; }

    /// <summary>
    /// Content root to reuse (SQLite message store and log directory), e.g.
    /// the <see cref="SimulatedMonzeHost.DataDirectory"/> of the stopped host
    /// on a restart. The host does not delete it.
    /// </summary>
    public DirectoryInfo? DataDirectory { get; init; }
}
