using System.Diagnostics;

namespace Monze.Campaign.Component;

/// <summary>
/// Runs the component scenarios one after another (they measure latency, so
/// they never share the machine with each other) and writes one artifact per
/// result to raw/component/. A scenario that throws is recorded as FAIL with
/// the exception type, so the report never loses a scenario silently.
/// </summary>
public static class ComponentRunner
{
    public static IReadOnlyList<(string Id, Func<ComponentContext, Task<IReadOnlyList<CampaignArtifact>>> Run)> Scenarios { get; } =
    [
        ("C-OUTBOX-CLAIM", OutboxClaimScenario.RunAsync),
        ("C-SCHED", ScheduleClaimScenario.RunAsync),
        ("C-SUMMARY-LEASE", SummaryLeaseScenario.RunAsync),
        ("C-INBOX", InboxRaceScenario.RunAsync),
        ("C-POOL", PoolScenario.RunAsync),
        ("C-SQLITE", SqliteStoreScenario.RunAsync),
        ("C-REDIS", RedisCacheScenario.RunAsync),
        ("C-MIGRATE", MigrateScenario.RunAsync)
    ];

    /// <summary>Returns 0 when every result passed, 1 when one failed, 2 when the rest were blocked.</summary>
    public static async Task<int> RunAsync(ComponentContext context, string rawDirectory, IReadOnlySet<string>? only)
    {
        var failed = false;
        var blocked = false;
        foreach (var (id, run) in Scenarios)
        {
            if (only is not null && !only.Contains(id))
            {
                continue;
            }

            Console.WriteLine($"[component] {id} …");
            var watch = Stopwatch.StartNew();
            IReadOnlyList<CampaignArtifact> artifacts;
            try
            {
                artifacts = await run(context);
            }
            catch (Exception error)
            {
                var message = error.Message.Split('\n')[0];
                artifacts = [new CampaignArtifact("component", id, id).Invariant("completed", "kịch bản chạy hết", $"{error.GetType().Name}: {message[..Math.Min(message.Length, 200)]}", pass: false)];
            }

            foreach (var artifact in artifacts)
            {
                artifact.Write(rawDirectory, watch.Elapsed);
                Console.WriteLine($"[component] {artifact.Id}: {artifact.Verdict} ({watch.Elapsed.TotalSeconds:0.0} s)");
                failed |= artifact.Verdict is not ("PASS" or "BLOCKED");
                blocked |= artifact.Verdict == "BLOCKED";
            }
        }

        return failed ? 1 : blocked ? 2 : 0;
    }
}
