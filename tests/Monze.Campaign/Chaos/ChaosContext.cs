using Monze.Simulator;

namespace Monze.Campaign.Chaos;

/// <summary>What a scenario's inject and heal actions can reach.</summary>
public sealed class ChaosContext(ChaosEnvironment environment, OffsetTimeProvider clock)
{
    public ChaosEnvironment Environment { get; } = environment;

    public OffsetTimeProvider Clock { get; } = clock;

    /// <summary>The running host; null in <see cref="ChaosScenario.BeforeStart"/>.</summary>
    public SimulatedMonzeHost? Host { get; set; }

    public SimulatedMonzeHost RequireHost() => Host ?? throw new InvalidOperationException("The host has not started yet.");
}
