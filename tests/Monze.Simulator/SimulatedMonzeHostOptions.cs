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

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(45);
}
