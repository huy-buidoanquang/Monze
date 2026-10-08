using System.Diagnostics;

namespace Monze.Campaign.Chaos;

/// <summary>
/// Runs the chaos matrix one scenario at a time on one
/// <see cref="ChaosEnvironment"/> (its own PostgreSQL and Redis containers)
/// and writes one artifact per scenario to raw/chaos/. Quick runs the subset
/// marked <see cref="ChaosScenario.Quick"/>; Full runs every scenario.
/// </summary>
public static class ChaosRunner
{
    public static async Task<int> RunAsync(string campaignId, int seed, bool full, string rawDirectory, IReadOnlySet<string>? only)
    {
        var scenarios = ChaosScenarios.All
            .Where(scenario => only is not null ? only.Contains(scenario.Id) : full || scenario.Quick)
            .ToList();
        var failed = false;
        var blocked = false;
        await using var environment = await ChaosEnvironment.StartAsync(campaignId);
        foreach (var scenario in scenarios)
        {
            Console.WriteLine($"[chaos] {scenario.Id} …");
            var watch = Stopwatch.StartNew();
            CampaignArtifact artifact;
            try
            {
                artifact = await ChaosRun.RunAsync(environment, scenario, seed);
            }
            catch (Exception error)
            {
                var message = error.Message.Split('\n')[0];
                artifact = new CampaignArtifact("chaos", scenario.Id, $"{scenario.Group}: {scenario.Title}")
                    .Invariant("completed", "kịch bản chạy hết", $"{error.GetType().Name}: {message[..Math.Min(message.Length, 200)]}", pass: false);
                await environment.HealAsync();
            }

            artifact.Write(rawDirectory, watch.Elapsed);
            Console.WriteLine($"[chaos] {scenario.Id}: {artifact.Verdict} ({watch.Elapsed.TotalSeconds:0.0} s)");
            failed |= artifact.Verdict is not ("PASS" or "BLOCKED" or "KNOWN_GAP");
            blocked |= artifact.Verdict == "BLOCKED";
        }

        return failed ? 1 : blocked ? 2 : 0;
    }
}
