using System.Diagnostics;
using System.Net;
using Monze.Infrastructure.Persistence;
using Monze.Testing.Harness;
using Npgsql;

namespace Monze.Campaign.Component;

/// <summary>
/// C-POOL: the production pool (64 connections, 5 s acquire timeout) behind
/// a fault proxy that adds about 200 ms per round trip, hit by 128
/// concurrent repository reads per round. No operation may time out and the
/// server may never see more than 64 connections from the pool.
/// </summary>
public static class PoolScenario
{
    private const int Operations = 128;
    private const string ApplicationName = "monze-pool-probe";
    private static readonly TimeSpan OneWay = TimeSpan.FromMilliseconds(100);

    public static async Task<IReadOnlyList<CampaignArtifact>> RunAsync(ComponentContext context)
    {
        var rounds = context.Full ? 10 : 3;
        var artifact = new CampaignArtifact("component", "C-POOL", $"Pool 64 + {Operations} thao tác đồng thời + ~200 ms/round trip qua proxy, {rounds} vòng");
        var (database, direct) = await context.CreateDatabaseAsync("c_pool");
        await using (database)
        await using (direct)
        {
            var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString);
            var target = new IPEndPoint(IPAddress.Parse(builder.Host!), builder.Port);
            await using var proxy = TcpFaultProxy.Start(target);
            proxy.Latency = OneWay;
            builder.Host = proxy.Endpoint.Address.ToString();
            builder.Port = proxy.Endpoint.Port;
            builder.ApplicationName = ApplicationName;
            await using var pooled = ComponentContext.ProductionDataSource(builder.ConnectionString);
            var authorization = new PostgresAuthorizationRepository(pooled);
            var operations = new LatencyHistogram();
            long errors = 0;
            long peak = 0;
            var errorTypes = new HashSet<string>(StringComparer.Ordinal);
            for (var round = 0; round < rounds; round++)
            {
                using var sampling = new CancellationTokenSource();
                var sampler = SamplePeakAsync(direct, sampling.Token);
                await Task.WhenAll(Enumerable.Range(0, Operations).Select(i => Task.Run(async () =>
                {
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        await authorization.IsAdminAsync(1, 100 + i, CancellationToken.None);
                    }
                    catch (Exception error) when (error is NpgsqlException or TimeoutException or InvalidOperationException)
                    {
                        Interlocked.Increment(ref errors);
                        lock (errorTypes)
                        {
                            errorTypes.Add(error.GetType().Name);
                        }
                    }

                    operations.Record(Stopwatch.GetElapsedTime(started));
                })));
                await sampling.CancelAsync();
                peak = Math.Max(peak, await sampler);
            }

            artifact.Metric("operations", operations.Count)
                .Latency("operation", operations)
                .Metric("peakServerConnections", peak)
                .Metric("errors", Interlocked.Read(ref errors))
                .Invariant("no-timeouts", "không thao tác nào lỗi hay timeout khi pool bão hoà", errorTypes.Count == 0 ? "0" : $"{errors:N0} ({string.Join(", ", errorTypes)})", errors == 0)
                .Invariant("pool-bound", "server thấy ≤ 64 kết nối từ pool", $"đỉnh {peak:N0}", peak is > 0 and <= 64);
        }

        return [artifact];
    }

    private static async Task<long> SamplePeakAsync(NpgsqlDataSource direct, CancellationToken cancellationToken)
    {
        // Samples pg_stat_activity over the direct (unproxied) data source.
        await using var connection = await direct.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name = @name;", connection);
        command.Parameters.AddWithValue("name", ApplicationName);
        long peak = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            peak = Math.Max(peak, Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None), System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return peak;
    }
}
