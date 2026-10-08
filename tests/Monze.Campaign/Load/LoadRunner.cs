using System.Diagnostics;
using Monze.Campaign.Component;

namespace Monze.Campaign.Load;

/// <summary>
/// Runs the full-process load matrix (variant × stage) one stage at a time
/// and writes one artifact per stage to raw/load/. Quick runs V3 at 1 and 10
/// registered clans with short windows; Full runs V1, V3 and V3L at 1, 10,
/// 100 and 1,000 registered clans.
/// </summary>
public static class LoadRunner
{
    public static async Task<int> RunAsync(ComponentContext context, string rawDirectory, IReadOnlyList<LoadVariant>? variants, IReadOnlyList<int>? stages)
    {
        var workload = context.Full ? LoadWorkload.Full : LoadWorkload.Quick;
        variants ??= context.Full ? LoadVariant.All : [LoadVariant.V3];
        stages ??= context.Full ? [1, 10, 100, 1_000] : [1, 10];
        var failed = false;
        var blocked = false;
        foreach (var variant in variants)
        {
            foreach (var stage in stages)
            {
                var id = $"L-{variant.Id}-S{stage}";
                Console.WriteLine($"[load] {id} …");
                var watch = Stopwatch.StartNew();
                CampaignArtifact artifact;
                try
                {
                    artifact = await LoadStage.RunAsync(context, variant, stage, workload);
                }
                catch (Exception error)
                {
                    var message = error.Message.Split('\n')[0];
                    artifact = new CampaignArtifact("load", id, id).Invariant("completed", "stage chạy hết", $"{error.GetType().Name}: {message[..Math.Min(message.Length, 200)]}", pass: false);
                }

                artifact.Write(rawDirectory, watch.Elapsed);
                Console.WriteLine($"[load] {id}: {artifact.Verdict} ({watch.Elapsed.TotalSeconds:0.0} s)");
                failed |= artifact.Verdict is not ("PASS" or "BLOCKED" or "KNOWN_GAP");
                blocked |= artifact.Verdict == "BLOCKED";
            }
        }

        return failed ? 1 : blocked ? 2 : 0;
    }
}
