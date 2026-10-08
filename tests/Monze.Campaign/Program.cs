using Monze.Campaign.Component;
using Monze.Campaign.Load;
using Monze.Testing;

namespace Monze.Campaign;

/// <summary>
/// The campaign performance runner. `component` measures the database,
/// Redis and SQLite paths in isolation; `load` drives the whole Monze host on
/// the offline simulator; `k6` cross-checks the load harness with k6. They run on the guarded campaign containers
/// (MONZE_TEST_POSTGRES, MONZE_TEST_POSTGRES_ALT, MONZE_REDIS_CONNECTION) and
/// write monze.artifact.v1 results to the campaign raw/ folder.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("component" or "load" or "k6"))
        {
            Console.Error.WriteLine("Usage: Monze.Campaign component [--full] [--only C-ID,...] [--artifacts <raw dir>]");
            Console.Error.WriteLine("       Monze.Campaign load [--full] [--variants V1,V3,V3L] [--stages 1,10,100,1000] [--artifacts <raw dir>]");
            Console.Error.WriteLine("       Monze.Campaign k6 [--full] [--k6 <path>] [--artifacts <raw dir>]");
            return 3;
        }

        var full = false;
        string? artifacts = CampaignEnvironment.ArtifactDirectory;
        HashSet<string>? only = null;
        List<LoadVariant>? variants = null;
        List<int>? stages = null;
        string? k6 = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--full":
                    full = true;
                    break;
                case "--only" when i + 1 < args.Length:
                    only = new HashSet<string>(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);
                    break;
                case "--variants" when i + 1 < args.Length:
                    variants = [];
                    foreach (var name in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var variant = LoadVariant.All.FirstOrDefault(candidate => candidate.Id == name);
                        if (variant is null)
                        {
                            Console.Error.WriteLine($"Unknown variant {name}.");
                            return 3;
                        }

                        variants.Add(variant);
                    }

                    break;
                case "--stages" when i + 1 < args.Length:
                    stages = [];
                    foreach (var value in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var stage) || stage < 1)
                        {
                            Console.Error.WriteLine($"Invalid stage {value}.");
                            return 3;
                        }

                        stages.Add(stage);
                    }

                    break;
                case "--k6" when i + 1 < args.Length:
                    k6 = args[++i];
                    break;
                case "--artifacts" when i + 1 < args.Length:
                    artifacts = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument {args[i]}.");
                    return 3;
            }
        }

        if (artifacts is null)
        {
            Console.Error.WriteLine($"Set --artifacts or {CampaignEnvironment.ArtifactDirectoryVariable}.");
            return 3;
        }

        var unknown = only?.Except(ComponentRunner.Scenarios.Select(static scenario => scenario.Id)).ToList();
        if (unknown is { Count: > 0 })
        {
            Console.Error.WriteLine($"Unknown scenario {string.Join(", ", unknown)}.");
            return 3;
        }

        var seed = int.TryParse(CampaignEnvironment.Seed, out var parsed) ? parsed : 1;
        var context = new ComponentContext(
            TestPostgres.ConnectionString,
            TestPostgres.IsAlternateConfigured ? TestPostgres.AlternateConnectionString : null,
            TestRedis.IsConfigured ? TestRedis.ConnectionString : null,
            full,
            seed);
        switch (args[0])
        {
            case "load":
                return await LoadRunner.RunAsync(context, artifacts, variants, stages);
            case "k6":
                var watch = System.Diagnostics.Stopwatch.StartNew();
                CampaignArtifact crossCheck;
                try
                {
                    crossCheck = await K6CrossCheck.RunAsync(context, K6CrossCheck.Locate(k6));
                }
                catch (Exception error)
                {
                    var message = error.Message.Split('\n')[0];
                    crossCheck = new CampaignArtifact("k6", "K6-CROSSCHECK", "K6-CROSSCHECK").Invariant("completed", "cross-check chạy hết", $"{error.GetType().Name}: {message[..Math.Min(message.Length, 200)]}", pass: false);
                }

                crossCheck.Write(artifacts, watch.Elapsed);
                Console.WriteLine($"[k6] K6-CROSSCHECK: {crossCheck.Verdict} ({watch.Elapsed.TotalSeconds:0.0} s)");
                return crossCheck.Verdict switch { "PASS" => 0, "BLOCKED" => 2, _ => 1 };
            default:
                return await ComponentRunner.RunAsync(context, artifacts, only);
        }
    }
}
