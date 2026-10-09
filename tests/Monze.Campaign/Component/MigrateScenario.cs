using System.Diagnostics;
using Monze.Infrastructure.Persistence;
using Monze.Testing.Harness;
using Monze.Testing.Postgres;
using Npgsql;

namespace Monze.Campaign.Component;

/// <summary>
/// C-MIGRATE: how long `Monze migrate` takes from an empty database and as a
/// no-op re-run, on the PostgreSQL 17 server and, when the campaign started
/// it, the PostgreSQL 16 server. Schema equality across versions is proven by
/// the integration migration matrix.
/// </summary>
public static class MigrateScenario
{
    public static async Task<IReadOnlyList<CampaignArtifact>> RunAsync(ComponentContext context)
    {
        var runs = context.Full ? 5 : 2;
        var artifact = new CampaignArtifact("component", "C-MIGRATE", $"Migrate từ DB rỗng và chạy lại, {runs} lần mỗi server");
        var servers = new List<(string Name, string Server)> { ("pg17", context.Server) };
        if (context.AlternateServer is { } alternate)
        {
            servers.Add(("pg16", alternate));
        }
        else
        {
            artifact.Note("Không có server PostgreSQL 16 (MONZE_TEST_POSTGRES_ALT); chỉ đo PostgreSQL 17.");
        }

        foreach (var (name, server) in servers)
        {
            var fromZero = new LatencyHistogram();
            var rerun = new LatencyHistogram();
            for (var run = 0; run < runs; run++)
            {
                await using var database = await CampaignDatabase.CreateEmptyAsync(server, $"c_migrate_{name}_{run}");
                await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
                var started = Stopwatch.GetTimestamp();
                await PostgresMigrator.ApplyAsync(dataSource, database.ConnectionString, CancellationToken.None);
                fromZero.Record(Stopwatch.GetElapsedTime(started));
                started = Stopwatch.GetTimestamp();
                await PostgresMigrator.ApplyAsync(dataSource, database.ConnectionString, CancellationToken.None);
                rerun.Record(Stopwatch.GetElapsedTime(started));
            }

            artifact.Latency(name + ".fromZero", fromZero)
                .Latency(name + ".rerun", rerun)
                .Invariant($"{name}.from-zero", "migrate từ DB rỗng ≤ 60 s", $"{fromZero.Max / 1000.0:0} ms", fromZero.Max <= 60_000_000)
                .Invariant($"{name}.rerun", "chạy lại là no-op ≤ 5 s", $"{rerun.Max / 1000.0:0} ms", rerun.Max <= 5_000_000);
        }

        return [artifact];
    }
}
