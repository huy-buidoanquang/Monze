namespace Monze.Campaign.Chaos;

/// <summary>
/// One chaos scenario: how to break a dependency (<see cref="Inject"/>), how
/// long the fault holds, how to heal it (by default every proxy back to pass,
/// every container running and the simulator's faults cleared) and what is
/// expected. <see cref="KnownDefect"/> names a registered defect whose
/// reproduction makes the listed invariants fail (KNOWN_GAP); with
/// <see cref="ServedDuringFault"/> commands must keep being answered while
/// the fault holds (an optional dependency such as Redis).
/// </summary>
public sealed record ChaosScenario(
    string Id,
    string Group,
    string Title,
    Func<ChaosContext, Task> Inject,
    TimeSpan Hold)
{
    public Func<ChaosContext, Task>? Heal { get; init; }

    /// <summary>Runs before the host starts (for faults present at startup).</summary>
    public Func<ChaosContext, Task>? BeforeStart { get; init; }

    public bool UsesRedis { get; init; }

    public bool ServedDuringFault { get; init; }

    public TimeSpan Observe { get; init; } = TimeSpan.FromSeconds(45);

    public string? KnownDefect { get; init; }

    public IReadOnlyList<string> KnownGapInvariants { get; init; } = [];

    /// <summary>Part of the quick profile's subset.</summary>
    public bool Quick { get; init; }

    /// <summary>Why the scenario cannot run in this revision (recorded as BLOCKED).</summary>
    public string? Blocked { get; init; }
}
