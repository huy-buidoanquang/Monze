using Monze.Campaign.Component;
using Monze.Testing;

namespace Monze.Campaign;

/// <summary>
/// The campaign performance runner. `component` measures the database,
/// Redis and SQLite paths in isolation on the guarded campaign containers
/// (MONZE_TEST_POSTGRES, MONZE_TEST_POSTGRES_ALT, MONZE_REDIS_CONNECTION) and
/// writes monze.artifact.v1 results to the campaign raw/ folder.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "component")
        {
            Console.Error.WriteLine("Usage: Monze.Campaign component [--full] [--only C-ID,...] [--artifacts <raw dir>]");
            return 3;
        }

        var full = false;
        string? artifacts = CampaignEnvironment.ArtifactDirectory;
        HashSet<string>? only = null;
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
        return await ComponentRunner.RunAsync(context, artifacts, only);
    }
}
