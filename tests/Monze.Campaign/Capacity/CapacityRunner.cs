using System.Diagnostics;
using Monze.Campaign.Component;

namespace Monze.Campaign.Capacity;

/// <summary>
/// Runs the backpressure and capacity scenarios (BP-1..8) one at a time and
/// writes one artifact per scenario to raw/capacity/. Quick runs reduced
/// sizes; Full runs the sizes of the plan.
/// </summary>
public static class CapacityRunner
{
    public static async Task<int> RunAsync(ComponentContext context, string rawDirectory, IReadOnlySet<string>? only)
    {
        var failed = false;
        var blocked = false;
        foreach (var (id, run) in CapacityScenarios.All)
        {
            if (only is not null && !only.Contains(id))
            {
                continue;
            }

            Console.WriteLine($"[capacity] {id} …");
            var watch = Stopwatch.StartNew();
            CampaignArtifact artifact;
            try
            {
                artifact = await run(context);
            }
            catch (Exception error)
            {
                var message = error.Message.Split('\n')[0];
                artifact = new CampaignArtifact("capacity", id, id).Invariant("completed", "kịch bản chạy hết", $"{error.GetType().Name}: {message[..Math.Min(message.Length, 200)]}", pass: false);
            }

            artifact.Write(rawDirectory, watch.Elapsed);
            Console.WriteLine($"[capacity] {id}: {artifact.Verdict} ({watch.Elapsed.TotalSeconds:0.0} s)");
            failed |= artifact.Verdict is not ("PASS" or "BLOCKED" or "KNOWN_GAP");
            blocked |= artifact.Verdict == "BLOCKED";
        }

        return failed ? 1 : blocked ? 2 : 0;
    }
}
