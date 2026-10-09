namespace Monze.Campaign.Chaos;

/// <summary>
/// What <see cref="AgentAiTraffic"/> observed: meeting cycles by final
/// session state (stuck = live, or summary pending with no retry
/// scheduled; deferred = summary pending with a retry scheduled), cycles
/// whose events were lost upstream, cycles started after recovery, burst
/// rooms, duplicated summary messages, failure notices, large-transcript
/// cycles, and AI calls by final answer with the token budget expected from
/// them and the one ai_usage holds.
/// </summary>
public sealed record AgentAiReport(
    int Cycles,
    int Posted,
    int Failed,
    int Stuck,
    int StuckClean,
    int Deferred,
    int Missing,
    int Lost,
    int AfterRecovery,
    int AfterRecoveryNotPosted,
    int Burst,
    int BurstPosted,
    int SummaryDuplicates,
    int SummaryMessages,
    long FailureNotices,
    int Large,
    int LargePosted,
    int AiSent,
    long AiAnswered,
    long AiAnsweredAfterRecovery,
    long AiProviderEmpty,
    long AiBusy,
    long AiBudgetExceeded,
    long AiRateLimited,
    long AiOther,
    long AiUnanswered,
    long ExpectedTokens,
    long ActualTokens);
