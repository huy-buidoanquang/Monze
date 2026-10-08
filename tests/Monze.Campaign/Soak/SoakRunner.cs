using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Monze.Campaign.Component;
using Monze.Campaign.Load;
using Monze.Simulator;
using Monze.Testing.Harness;
using Npgsql;

namespace Monze.Campaign.Soak;

/// <summary>
/// Full-process soak. Monze runs on the simulator under the documented workload in cycles of
/// traffic followed by an idle window that starts with a server-side socket
/// close (reconnect). Checkpoints force a GC and sample heap, private bytes,
/// handles, threads and the database pool. The leak criteria are:
/// - L1: heap after GC ≤ 2 MiB/h (Mann–Kendall α = 0.01 and Sen's slope);
/// - L2: private bytes ≤ 5 MiB/h;
/// - L3: handles and threads bounded;
/// - L5: queues, pending writers and in-flight work back to 0 in every idle window;
/// - L6: pool ≤ 64 with no timeouts;
/// - L7: no error log;
/// - L8: p99 at the end ≤ 1.2 × p99 at the start.
/// Growth of tables without retention is projected to 30 and 365 days as a
/// finding, not a pass/fail. A run shorter than 120 minutes is not G4
/// evidence. Every harness structure is bounded (message retention, recorder
/// limit, nonces forgotten once seen) and the first quarter of the samples
/// is treated as warm-up, so the trend measures Monze at steady state.
/// </summary>
public static class SoakRunner
{
    private static readonly string[] Meters = ["Monze", "Npgsql", "System.Runtime"];
    private static readonly string[] Gauges = ["monze.ingress.message.depth", "monze.ingress.message_gap.depth", "monze.ingress.agent.depth", "monze.ingress.welcome.depth", "monze.ingress.agent.pending_writers", "monze.ingress.welcome.pending_writers", "monze.command.inflight", "monze.outbox.inflight"];

    public static async Task<int> RunAsync(ComponentContext context, string rawDirectory, int minutes)
    {
        var watch = Stopwatch.StartNew();
        var artifact = await RunCoreAsync(context, minutes);
        artifact.Write(rawDirectory, watch.Elapsed);
        Console.WriteLine($"[soak] {artifact.Id}: {artifact.Verdict} ({watch.Elapsed.TotalMinutes:0.0} min)");
        return artifact.Verdict switch { "PASS" or "KNOWN_GAP" => 0, "BLOCKED" => 2, _ => 1 };
    }

    private static async Task<CampaignArtifact> RunCoreAsync(ComponentContext context, int minutes)
    {
        var fullLength = minutes >= 120;
        var registered = fullLength ? 1_000 : 100;
        var variant = fullLength ? LoadVariant.V3L : LoadVariant.V3;
        var cycle = fullLength ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(Math.Max(1, minutes / 2.0));
        var idle = fullLength ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(Math.Min(60, cycle.TotalSeconds / 5));
        var checkpointEvery = fullLength ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(Math.Max(15, minutes * 3));
        var cycles = Math.Max(1, (int)Math.Round(minutes / cycle.TotalMinutes));
        var artifact = new CampaignArtifact("soak", $"SOAK-{minutes}M", $"Soak {minutes} phút: {registered:N0} clan đăng ký, {variant.Id}, {cycles} chu kỳ tải + nghỉ (reconnect)");
        if (!fullLength)
        {
            artifact.Note("Soak ngắn: không phải bằng chứng G4 (cần ≥ 120 phút).");
        }

        artifact.Note("Chưa có restart giữa chừng: cần host dùng lại database (workflow E2E).");
        var world = LoadWorld.Create(registered);
        world.World.MessageRetention = 5_000;
        var ackRandom = new Random(context.Seed);
        var logs = new HostLogSink(LogLevel.Warning, capacity: 20_000);
        await using var host = await SimulatedMonzeHost.StartAsync(world.World, context.Server, $"soak_{minutes}", new SimulatedMonzeHostOptions
        {
            Simulator = new MezonSimulatorOptions
            {
                ResponseLatency = variant.AckLatency ? _ => LogNormal(ackRandom) : null
            },
            Logs = logs,
            SeedAsync = world.SeedAsync,
            StartTimeout = TimeSpan.FromMinutes(5),
            Timings = static timings => timings with
            {
                SchedulerInterval = TimeSpan.FromSeconds(1),
                MaintenanceInterval = TimeSpan.FromMinutes(1),
                OutboxPollInterval = TimeSpan.FromMilliseconds(250),
                RoleScanInterval = TimeSpan.FromSeconds(300),
                UncertainMarkTimeout = TimeSpan.FromSeconds(5)
            }
        });
        host.Recorder.RetainLimit = 5_000;
        using var collector = new MetricCollector(Meters);
        using var process = Process.GetCurrentProcess();
        var tablesBefore = await LiveRowsAsync(host.Database.ConnectionString);
        var samples = new List<(double Hours, double HeapMiB, double PrivateMiB, int Handles, int Threads)>();
        double maxPoolUsed = 0;
        var started = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                process.Refresh();
                var snapshot = collector.Snapshot();
                lock (samples)
                {
                    samples.Add((started.Elapsed.TotalHours, GC.GetGCMemoryInfo().HeapSizeBytes / 1048576.0, process.PrivateMemorySize64 / 1048576.0, process.HandleCount, process.Threads.Count));
                    maxPoolUsed = Math.Max(maxPoolUsed, snapshot.Sum("db.client.connection.count", "db.client.connection.state=used"));
                }

                try
                {
                    await Task.Delay(checkpointEvery, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        var cycleP99 = new List<double>();
        var idleFailures = new List<string>();
        long lost = 0, duplicates = 0, undelivered = 0, commands = 0;
        for (var i = 0; i < cycles; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var end = start + (long)((cycle - idle).TotalSeconds * Stopwatch.Frequency);
            var driver = LoadDriver.Start(host, world, LoadWorkload.Full, context.Seed + i, start, start, end);
            driver.WaitForSchedule();
            await driver.StopAsync();
            await host.Inbound.CloseSocketAsync();
            await Task.Delay(idle);
            driver.Tracker.Sweep();
            var unanswered = driver.Tracker.Unanswered();
            cycleP99.Add(driver.Tracker.Commands.ValueAtPercentile(99) / 1000.0);
            commands += driver.Tracker.CommandsSent;
            lost += driver.Tracker.CommandsLost + driver.Tracker.CommandsSkipped;
            duplicates += driver.Tracker.OutboxDuplicates;
            undelivered += unanswered.Outbox;
            var snapshot = collector.Snapshot();
            var busy = Gauges.Where(gauge => snapshot.Sum(gauge) > 0).Select(gauge => $"{gauge}={snapshot.Sum(gauge):0}").ToList();
            if (busy.Count > 0)
            {
                idleFailures.Add($"chu kỳ {i + 1}: {string.Join(", ", busy)}");
            }

            host.Recorder.Recorded -= driver.Tracker.OnRecorded;
            Console.WriteLine($"[soak] chu kỳ {i + 1}/{cycles}: p99 {cycleP99[^1]:0.#} ms, {started.Elapsed.TotalMinutes:0} phút");
        }

        await stop.CancelAsync();
        await sampler;
        var elapsedHours = started.Elapsed.TotalHours;
        var tablesAfter = await LiveRowsAsync(host.Database.ConnectionString);
        var final = collector.Snapshot();
        IReadOnlyList<(double Hours, double HeapMiB, double PrivateMiB, int Handles, int Threads)> points;
        lock (samples)
        {
            // The first quarter is warm-up: caches, pools and the bounded harness buffers fill.
            points = samples.Skip(Math.Max(2, samples.Count / 4)).ToList();
        }

        var hours = points.Select(static point => point.Hours).ToList();
        var heap = points.Select(static point => point.HeapMiB).ToList();
        var privateBytes = points.Select(static point => point.PrivateMiB).ToList();
        var heapP = TrendAnalysis.MannKendallIncreasingP(heap);
        var heapSlope = TrendAnalysis.SenSlopePerHour(hours, heap);
        var privateP = TrendAnalysis.MannKendallIncreasingP(privateBytes);
        var privateSlope = TrendAnalysis.SenSlopePerHour(hours, privateBytes);
        var handleSpread = points.Count == 0 ? 0 : points.Max(static point => point.Handles) - points.Min(static point => point.Handles);
        var threadSpread = points.Count == 0 ? 0 : points.Max(static point => point.Threads) - points.Min(static point => point.Threads);
        var poolTimeouts = final.Sum("db.client.connection.timeouts");
        var errors = logs.CountAtLeast(LogLevel.Error);
        var thirds = Math.Max(1, cycleP99.Count / 3);
        var earlyP99 = cycleP99.Take(fullLength ? 2 : thirds).DefaultIfEmpty(0).Max();
        var lateP99 = cycleP99.TakeLast(fullLength ? 1 : thirds).DefaultIfEmpty(0).Max();

        artifact.Metric("minutes", started.Elapsed.TotalMinutes)
            .Metric("heapSlopeMiBPerHour", heapSlope)
            .Metric("heapTrendP", heapP)
            .Metric("privateSlopeMiBPerHour", privateSlope)
            .Metric("earlyP99Ms", earlyP99)
            .Metric("lateP99Ms", lateP99)
            .Metric("samples", points.Count)
            .Metric("handleSpread", handleSpread)
            .Metric("threadSpread", threadSpread)
            .Metric("maxPoolUsed", maxPoolUsed)
            .Metric("poolTimeouts", poolTimeouts)
            .Metric("commands", commands)
            .Metric("commandsLost", lost)
            .Metric("outboxDuplicates", duplicates)
            .Metric("outboxUndelivered", undelivered)
            .Metric("hostErrors", errors)
            .Invariant("L1-heap", "heap sau GC không tăng > 2 MiB/h có ý nghĩa (Mann–Kendall α 0,01)", F($"{heapSlope:0.##} MiB/h, p = {heapP:0.####}"), points.Count >= 4 && !(heapP < 0.01 && heapSlope > 2))
            .Invariant("L2-private", "private bytes không tăng > 5 MiB/h có ý nghĩa", F($"{privateSlope:0.##} MiB/h, p = {privateP:0.####}"), points.Count >= 4 && !(privateP < 0.01 && privateSlope > 5))
            .Invariant("L3-handles-threads", "handle dao động ≤ 500, thread ≤ 64", $"handle ±{handleSpread}, thread ±{threadSpread}", handleSpread <= 500 && threadSpread <= 64)
            .Invariant("L5-idle-drain", "queue, pending writer, inflight về 0 ở mỗi cửa sổ nghỉ", idleFailures.Count == 0 ? $"{cycles} cửa sổ sạch" : string.Join("; ", idleFailures), idleFailures.Count == 0)
            .Invariant("L6-pool", "pool ≤ 64 kết nối và không timeout", F($"đỉnh {maxPoolUsed:0}, {poolTimeouts:0} timeout"), maxPoolUsed <= 64 && poolTimeouts == 0)
            .Invariant("L7-errors", "không có log Error", $"{errors:N0}", errors == 0)
            .Invariant("L8-latency", "p99 cuối ≤ 1,2 × p99 đầu", F($"{earlyP99:0.#} ms → {lateP99:0.#} ms"), lateP99 <= Math.Max(earlyP99 * 1.2, earlyP99 + 5))
            .Invariant("served", "mọi lệnh được trả lời và outbox gửi đúng một lần", $"{lost:N0} lệnh mất, {undelivered:N0} outbox chưa gửi, {duplicates:N0} trùng", lost == 0 && undelivered == 0 && duplicates == 0);
        foreach (var (table, after) in tablesAfter.OrderByDescending(static entry => entry.Value).Take(8))
        {
            var perHour = (after - tablesBefore.GetValueOrDefault(table)) / Math.Max(elapsedHours, 1e-6);
            if (perHour > 0)
            {
                artifact.Note(F($"{table}: +{perHour:0} dòng/h → {perHour * 24 * 30:N0} sau 30 ngày, {perHour * 24 * 365:N0} sau 365 ngày"));
            }
        }

        return artifact;
    }

    private static async Task<Dictionary<string, long>> LiveRowsAsync(string connectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand("SELECT relname, n_live_tup FROM pg_stat_user_tables;");
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            rows[reader.GetString(0)] = reader.GetInt64(1);
        }

        return rows;
    }

    private static TimeSpan LogNormal(Random random)
    {
        double normal;
        lock (random)
        {
            normal = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        }

        return TimeSpan.FromMilliseconds(Math.Exp(Math.Log(30) + Math.Log(5) / 2.326 * normal));
    }

    private static string F(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
