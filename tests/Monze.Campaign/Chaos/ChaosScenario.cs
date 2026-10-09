using Monze.Hosting;

namespace Monze.Campaign.Chaos;

/// <summary>
/// One chaos scenario: how to break a dependency (<see cref="Inject"/>), how
/// long the fault holds, how to heal it (by default every proxy back to pass,
/// every container running, the simulator's and the HTTP fake's faults
/// cleared and the Agent/AI traffic back to normal) and what is expected.
/// <see cref="KnownDefect"/> names a registered defect whose reproduction
/// makes the listed invariants fail (KNOWN_GAP); with
/// <see cref="ServedDuringFault"/> commands must keep being answered while
/// the fault holds (an optional dependency such as Redis or the AI provider).
/// <see cref="UsesHttp"/> adds the HTTP fake (Agent SSE, transcript, AI) and
/// Agent meeting cycles and AI commands as background traffic.
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

    /// <summary>Further registered defects a scenario reproduces at once, each with the invariant it makes fail.</summary>
    public IReadOnlyList<(string Defect, string Invariant)> OtherKnownGaps { get; init; } = [];

    /// <summary>Part of the quick profile's subset.</summary>
    public bool Quick { get; init; }

    /// <summary>Why the scenario cannot run in this revision (recorded as BLOCKED).</summary>
    public string? Blocked { get; init; }

    /// <summary>Start the HTTP fake and the Agent/AI background traffic.</summary>
    public bool UsesHttp { get; init; }

    /// <summary>Adjusts the chaos worker timings (after the run's defaults).</summary>
    public Func<MonzeWorkerTimings, MonzeWorkerTimings>? Timings { get; init; }

    /// <summary>Several instances may receive the same input: every command must still be answered once.</summary>
    public bool ChecksSingleAnswer { get; init; }

    /// <summary>
    /// Pending summary retries of the Agent clans are made due at once while
    /// the run drains (a time warp on the database), so retry outcomes that
    /// take minutes of backoff are observed within the run.
    /// </summary>
    public bool WarpSummaryRetries { get; init; }

    /// <summary>Scenario-specific checks after the drain (adds metrics and invariants to the artifact).</summary>
    public Func<ChaosContext, CampaignArtifact, Task>? Verify { get; init; }
}
