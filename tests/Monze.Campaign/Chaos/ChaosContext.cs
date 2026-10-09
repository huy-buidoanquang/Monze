using Microsoft.Extensions.Logging;
using Monze.Campaign.Load;
using Monze.Simulator;

namespace Monze.Campaign.Chaos;

/// <summary>
/// What a scenario's inject and heal actions can reach: the environment,
/// the app clock, the running host (the primary, or the one that replaced
/// it after a restart), further instances on the same database and
/// platform, the HTTP fake and the Agent/AI and Redis traffic of the run.
/// </summary>
public sealed class ChaosContext(ChaosEnvironment environment, OffsetTimeProvider clock)
{
    private readonly List<SimulatedMonzeHost> _instances = [];
    private readonly List<HostLogSink> _sinks = [];

    public ChaosEnvironment Environment { get; } = environment;

    public OffsetTimeProvider Clock { get; } = clock;

    /// <summary>The host serving the run; null in <see cref="ChaosScenario.BeforeStart"/>.</summary>
    public SimulatedMonzeHost? Host { get; set; }

    public LoadWorld? World { get; set; }

    /// <summary>The HTTP fake (Agent SSE, transcript, AI) of a <see cref="ChaosScenario.UsesHttp"/> scenario.</summary>
    public SimHttpHost? Http { get; set; }

    public AgentAiTraffic? Agent { get; set; }

    public RedisWelcomeTraffic? Redis { get; set; }

    /// <summary>Every log sink of the run (one per host instance).</summary>
    public IReadOnlyList<HostLogSink> Sinks => _sinks;

    /// <summary>Hosts started in addition to the primary one (second instances and restarts).</summary>
    public IReadOnlyList<SimulatedMonzeHost> Instances => _instances;

    /// <summary>The options the primary host was started with; further instances reuse them.</summary>
    internal SimulatedMonzeHostOptions? HostOptions { get; set; }

    public SimulatedMonzeHost RequireHost() => Host ?? throw new InvalidOperationException("The host has not started yet.");

    public SimHttpHost RequireHttp() => Http ?? throw new InvalidOperationException("The scenario has no HTTP fake (set UsesHttp).");

    public AgentAiTraffic RequireAgent() => Agent ?? throw new InvalidOperationException("The scenario has no Agent/AI traffic (set UsesHttp).");

    internal void AddSink(HostLogSink sink) => _sinks.Add(sink);

    /// <summary>Errors (or another level) logged by every host of the run.</summary>
    public long LoggedAtLeast(LogLevel level) => _sinks.Sum(sink => sink.CountAtLeast(level));

    /// <summary>
    /// Starts another Monze instance on the same database, platform (bot
    /// token, simulator) and HTTP fake, with its own log sink; with
    /// <paramref name="sameDataDirectory"/> it also reuses the current
    /// host's SQLite store (a restart rather than a second machine).
    /// </summary>
    public async Task<SimulatedMonzeHost> StartInstanceAsync(bool sameDataDirectory)
    {
        var current = RequireHost();
        var options = HostOptions ?? throw new InvalidOperationException("The primary host options are unknown.");
        var sink = new HostLogSink(LogLevel.Warning, capacity: 20_000);
        _sinks.Add(sink);
        var instance = await SimulatedMonzeHost.StartAsync(current.World, Environment.Server, "unused", options with
        {
            Logs = sink,
            SeedAsync = null,
            Prepare = null,
            Database = current.Database,
            SharedSimulator = current.Simulator,
            DataDirectory = sameDataDirectory ? current.DataDirectory : null
        });
        instance.Recorder.RetainLimit = current.Recorder.RetainLimit;
        _instances.Add(instance);
        return instance;
    }

    /// <summary>
    /// The default heal: the simulator's and the HTTP fake's faults cleared,
    /// the Agent/AI traffic back to normal delivery and pace, every proxy back
    /// to pass and every container running.
    /// </summary>
    public async Task HealDefaultsAsync()
    {
        RequireHost().Simulator.Faults.Clear();
        Http?.Faults.Clear();
        if (Agent is { } agent)
        {
            agent.Delivery = SimSseDelivery.Normal;
            agent.Reorder = false;
            agent.LoseEnded = false;
            agent.LargeTranscripts = false;
            agent.AiInterval = TimeSpan.FromMilliseconds(1_500);
        }

        await Environment.HealAsync();
    }

    /// <summary>Starts a host on the stopped (or killed) host's database and data directory and makes it current.</summary>
    public async Task RestartAsync()
    {
        Host = await StartInstanceAsync(sameDataDirectory: true);
    }

    /// <summary>Stops and disposes every further instance, newest first (the primary owns the database and simulator).</summary>
    internal async Task DisposeInstancesAsync()
    {
        for (var i = _instances.Count - 1; i >= 0; i--)
        {
            await _instances[i].DisposeAsync();
        }

        _instances.Clear();
    }
}
