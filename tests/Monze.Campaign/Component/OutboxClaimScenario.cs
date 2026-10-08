using System.Collections.Concurrent;
using System.Diagnostics;
using Monze.Infrastructure.Persistence;
using Monze.Testing.Harness;
using Npgsql;

namespace Monze.Campaign.Component;

/// <summary>
/// C-OUTBOX-CLAIM and C-SKIPLOCKED: workers drain a backlog of due outbox
/// rows that sits on top of a history of sent rows (the outbox has no
/// retention) through the production claim and completion queries. Every
/// row must be claimed exactly once and, with the largest worker count, no
/// worker may starve. After the burst the workers keep polling an empty
/// backlog, as the 250 ms outbox poll does, with the statistics the burst
/// left behind (autovacuum is off on this throwaway database, standing in
/// for the gap between two autoanalyze runs). Known gap CAND-17: the planner
/// walks the primary key through the sent history on most claims and on
/// every such poll, so claim latency grows with the history.
/// </summary>
public static class OutboxClaimScenario
{
    private const int HistoryFactor = 4;
    private const int IdlePolls = 20;

    public static async Task<IReadOnlyList<CampaignArtifact>> RunAsync(ComponentContext context)
    {
        var rows = context.Full ? 50_000 : 10_000;
        int[] workerCounts = context.Full ? [1, 2, 4, 8] : [1, 8];
        var claim = new CampaignArtifact("component", "C-OUTBOX-CLAIM", $"Outbox claim: {rows:N0} dòng due trên {rows * HistoryFactor:N0} dòng đã gửi, {string.Join('/', workerCounts)} worker, rồi poll rảnh");
        var fairness = new CampaignArtifact("component", "C-SKIPLOCKED", $"SKIP LOCKED fairness với {workerCounts[^1]} worker");
        claim.Metric("rows", rows).KnownGap("CAND-17", "idle-poll-bounded");

        // Largest worker count first, so the report's first metrics are the idle-poll ones.
        foreach (var workers in Enumerable.Reverse(workerCounts))
        {
            var last = workers == workerCounts[^1];
            var run = await DrainAsync(context, rows, workers, last);
            var prefix = $"w{workers}.";
            if (last)
            {
                var table = rows * (HistoryFactor + 1);
                claim.Metric("idleTuplesPerPoll", run.IdleTuplesPerPoll)
                    .Metric("idleFreshStatsTuplesPerPoll", run.IdleFreshTuplesPerPoll)
                    .Latency("idlePoll", run.IdlePolls!)
                    .Invariant(
                        "idle-poll-bounded",
                        $"poll rảnh sau burst đọc ≤ 1 % bảng ({table / 100:N0} tuple)",
                        $"{run.IdleTuplesPerPoll:N0} tuple/poll trên bảng {table:N0} dòng (thống kê mới: {run.IdleFreshTuplesPerPoll:N0})",
                        run.IdleTuplesPerPoll <= table / 100);
            }

            claim.Metric(prefix + "rowsPerSecond", rows / run.Elapsed.TotalSeconds)
                .Metric(prefix + "tuplesPerClaimedRow", (double)run.TuplesRead / rows)
                .Latency(prefix + "claim", run.Claims)
                .Latency(prefix + "complete", run.Completions)
                .Invariant($"{prefix}exactly-once", $"{rows:N0} dòng, mỗi dòng claim một lần", $"{run.Claimed:N0} dòng, {run.Duplicates:N0} trùng, {run.Sent:N0} sent", run.Claimed == rows && run.Duplicates == 0 && run.Sent == rows)
                .P99AtMost($"{prefix}claim-p99", run.Claims, 50, "SLO đề xuất DB p99")
                .KnownGap("CAND-17", $"{prefix}claim-p99");
            if (last)
            {
                var perWorker = run.PerWorker;
                var sum = perWorker.Sum(static count => (double)count);
                var squares = perWorker.Sum(static count => (double)count * count);
                var jain = squares == 0 ? 0 : sum * sum / (perWorker.Length * squares);
                fairness.Metric("workers", workers).Metric("jainIndex", jain).Metric("minRows", perWorker.Min()).Metric("maxRows", perWorker.Max());
                fairness.Invariant("no-starvation", "mỗi worker claim được ít nhất một batch", string.Join('/', perWorker), perWorker.All(static count => count > 0));
            }
        }

        return [claim, fairness];
    }

    private static async Task<DrainResult> DrainAsync(ComponentContext context, int rows, int workers, bool measureIdle)
    {
        var (database, seeding) = await context.CreateDatabaseAsync($"c_outbox_w{workers}");
        await using (database)
        {
            await using (seeding)
            {
                await ComponentContext.ExecuteAsync(seeding, $"""
                    ALTER TABLE outbox_delivery SET (autovacuum_enabled = false);
                    INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, status, external_message_id, due_at)
                    SELECT (g % 100) + 1, 10, 'Announcement', 'history:' || g, 'b', 'sent', 2000000000 + g, now() - interval '1 day'
                    FROM generate_series(1, {rows * HistoryFactor}) g;
                    INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, due_at)
                    SELECT (g % 100) + 1, 10, 'Announcement', 'load:' || g, 'b', now() - interval '1 second'
                    FROM generate_series(1, {rows}) g;
                    ANALYZE outbox_delivery;
                    """);
            }

            var before = await TuplesReadAsync(database.ConnectionString);
            var claimed = new ConcurrentDictionary<long, int>();
            var claims = new LatencyHistogram();
            var completions = new LatencyHistogram();
            var perWorker = new long[workers];
            long duplicates = 0;
            var watch = Stopwatch.StartNew();
            await using (var dataSource = ComponentContext.ProductionDataSource(database.ConnectionString))
            {
                var repository = new PostgresOutboxRepository(dataSource);
                await Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
                {
                    while (true)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var batch = await repository.ClaimDueOutboxAsync(CancellationToken.None);
                        claims.Record(Stopwatch.GetElapsedTime(started));
                        if (batch.Count == 0)
                        {
                            return;
                        }

                        perWorker[worker] += batch.Count;
                        foreach (var item in batch)
                        {
                            if (!claimed.TryAdd(item.Id, worker))
                            {
                                Interlocked.Increment(ref duplicates);
                            }

                            started = Stopwatch.GetTimestamp();
                            await repository.CompleteOutboxAsync(item.Id, item.LeaseToken, 1_000_000_000 + item.Id, failed: false, CancellationToken.None);
                            completions.Record(Stopwatch.GetElapsedTime(started));
                        }
                    }
                })));
                watch.Stop();
            }

            var drained = await TuplesReadAsync(database.ConnectionString);
            LatencyHistogram? idle = null;
            long idleTuples = 0;
            long idleFreshTuples = 0;
            if (measureIdle)
            {
                idle = new LatencyHistogram();
                idleTuples = await IdlePollsAsync(database.ConnectionString, idle) / IdlePolls;
                await using (var analyze = NpgsqlDataSource.Create(database.ConnectionString))
                {
                    await ComponentContext.ExecuteAsync(analyze, "ANALYZE outbox_delivery;");
                }

                idleFreshTuples = await IdlePollsAsync(database.ConnectionString, new LatencyHistogram()) / IdlePolls;
            }

            await using var counting = NpgsqlDataSource.Create(database.ConnectionString);
            var sent = await ComponentContext.CountAsync(counting, "SELECT count(*) FROM outbox_delivery WHERE status = 'sent' AND dedupe_key LIKE 'load:%';");
            return new DrainResult(watch.Elapsed, claims, completions, claimed.Count, Interlocked.Read(ref duplicates), sent, drained - before, perWorker, idle, idleTuples, idleFreshTuples);
        }
    }

    /// <summary>Polls an empty backlog <see cref="IdlePolls"/> times and returns the tuples those polls read.</summary>
    private static async Task<long> IdlePollsAsync(string connectionString, LatencyHistogram latency)
    {
        var before = await TuplesReadAsync(connectionString);
        await using (var dataSource = ComponentContext.ProductionDataSource(connectionString))
        {
            var repository = new PostgresOutboxRepository(dataSource);
            for (var i = 0; i < IdlePolls; i++)
            {
                var started = Stopwatch.GetTimestamp();
                var batch = await repository.ClaimDueOutboxAsync(CancellationToken.None);
                latency.Record(Stopwatch.GetElapsedTime(started));
                if (batch.Count != 0)
                {
                    throw new InvalidOperationException("The backlog was not empty.");
                }
            }
        }

        return await TuplesReadAsync(connectionString) - before;
    }

    /// <summary>
    /// Heap tuples read from outbox_delivery so far. Backends flush their
    /// table statistics when they exit, so callers dispose their data source
    /// first; this waits briefly for those exits and reads over a fresh
    /// unpooled connection.
    /// </summary>
    private static async Task<long> TuplesReadAsync(string connectionString)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(seq_tup_read, 0) + COALESCE(idx_tup_fetch, 0) FROM pg_stat_user_tables WHERE relname = 'outbox_delivery';",
            connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record DrainResult(
        TimeSpan Elapsed,
        LatencyHistogram Claims,
        LatencyHistogram Completions,
        long Claimed,
        long Duplicates,
        long Sent,
        long TuplesRead,
        long[] PerWorker,
        LatencyHistogram? IdlePolls,
        long IdleTuplesPerPoll,
        long IdleFreshTuplesPerPoll);
}
