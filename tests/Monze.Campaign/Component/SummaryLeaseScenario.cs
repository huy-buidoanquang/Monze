using System.Collections.Concurrent;
using System.Diagnostics;
using Monze.Infrastructure.Persistence;
using Monze.Testing.Harness;

namespace Monze.Campaign.Component;

/// <summary>
/// C-SUMMARY-LEASE: eight maintenance workers lease pending meeting
/// summaries, fail about 60 % of attempts (deterministically per room and
/// attempt) and store the rest. The backoff is warped forward only on this
/// throwaway database. No room may be held by two workers at once, every
/// room must end posted or summary_failed, and no room may exceed 8 attempts.
/// </summary>
public static class SummaryLeaseScenario
{
    private const int Workers = 8;
    private const int MaxAttempts = 8;

    public static async Task<IReadOnlyList<CampaignArtifact>> RunAsync(ComponentContext context)
    {
        var rooms = context.Full ? 2_000 : 500;
        var artifact = new CampaignArtifact("component", "C-SUMMARY-LEASE", $"Summary lease: {rooms:N0} meeting chờ tóm tắt, {Workers} worker, ~60 % lần thử lỗi");
        var (database, dataSource) = await context.CreateDatabaseAsync("c_summary");
        await using (database)
        await using (dataSource)
        {
            await ComponentContext.ExecuteAsync(dataSource, $"""
                INSERT INTO meeting_session(clan_id, text_channel_id, requester_id, status, room_id, direct_agent)
                SELECT (g % 100) + 1, 10, 5, 'summary_pending', 'load-room-' || g, TRUE
                FROM generate_series(1, {rooms}) g;
                ANALYZE meeting_session;
                """);
            var repository = new PostgresMeetingRepository(dataSource);
            var holders = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var claims = new LatencyHistogram();
            long overlaps = 0;
            long lostStores = 0;
            long warps = 0;
            var watch = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
            {
                while (true)
                {
                    var started = Stopwatch.GetTimestamp();
                    var pending = await repository.ListPendingSummariesAsync(1, CancellationToken.None);
                    claims.Record(Stopwatch.GetElapsedTime(started));
                    if (pending.Count == 0)
                    {
                        if (await ComponentContext.CountAsync(dataSource, "SELECT count(*) FROM meeting_session WHERE status = 'summary_pending';") == 0)
                        {
                            return;
                        }

                        // Skip the 5 s..300 s retry backoff: this database exists only for this scenario.
                        Interlocked.Increment(ref warps);
                        await ComponentContext.ExecuteAsync(dataSource, "UPDATE meeting_session SET summary_next_attempt_at = now() WHERE status = 'summary_pending' AND summary_next_attempt_at > now();");
                        continue;
                    }

                    var item = pending[0];
                    if (!holders.TryAdd(item.RoomId, worker))
                    {
                        Interlocked.Increment(ref overlaps);
                        continue;
                    }

                    try
                    {
                        if (Fails(item.RoomId, item.Attempts, context.Seed))
                        {
                            await repository.RecordSummaryRetryAsync(item.RoomId, "component failure", CancellationToken.None, item.LeaseToken);
                        }
                        else if (!await repository.StoreSummaryAsync(item.RoomId, "tóm tắt", null, CancellationToken.None, item.LeaseToken))
                        {
                            Interlocked.Increment(ref lostStores);
                        }
                    }
                    finally
                    {
                        holders.TryRemove(item.RoomId, out _);
                    }
                }
            })));
            watch.Stop();

            var posted = await ComponentContext.CountAsync(dataSource, "SELECT count(*) FROM meeting_session s JOIN meeting_summary m ON m.session_id = s.id WHERE s.status = 'posted';");
            var failed = await ComponentContext.CountAsync(dataSource, "SELECT count(*) FROM meeting_session WHERE status = 'summary_failed';");
            var failedWrong = await ComponentContext.CountAsync(dataSource, $"""
                SELECT count(*) FROM meeting_session s
                WHERE s.status = 'summary_failed'
                  AND (s.summary_attempts <> {MaxAttempts}
                       OR (SELECT count(*) FROM outbox_delivery o WHERE o.dedupe_key = 'meeting-summary-failed:' || s.id::text) <> 1);
                """);
            var maxAttempts = await ComponentContext.CountAsync(dataSource, "SELECT COALESCE(max(summary_attempts), 0) FROM meeting_session;");
            var violations = await MonzeInvariants.CheckAsync(dataSource, TimeSpan.FromMinutes(1));
            artifact.Metric("rooms", rooms)
                .Metric("posted", posted)
                .Metric("summaryFailed", failed)
                .Latency("lease", claims)
                .Metric("backoffWarps", Interlocked.Read(ref warps))
                .Metric("seconds", watch.Elapsed.TotalSeconds)
                .Invariant("single-holder", "không room nào bị hai worker giữ cùng lúc", $"{overlaps:N0} chồng lease", overlaps == 0)
                .Invariant("lease-honoured", "mọi lần lưu với lease hợp lệ đều thành công", $"{lostStores:N0} lần lưu bị từ chối", lostStores == 0)
                .Invariant("terminal", $"{rooms:N0} room kết thúc posted hoặc summary_failed", $"{posted:N0} posted + {failed:N0} failed", posted + failed == rooms)
                .Invariant("attempt-bound", $"attempts ≤ {MaxAttempts}; summary_failed có đúng {MaxAttempts} lần và một thông báo lỗi", $"max {maxAttempts}, {failedWrong:N0} dòng failed sai", maxAttempts <= MaxAttempts && failedWrong == 0)
                .DbInvariants(violations)
                .P99AtMost("lease-p99", claims, 50, "SLO đề xuất DB p99");
        }

        return [artifact];
    }

    // FNV-1a, stable across processes (string.GetHashCode is randomized per process).
    private static bool Fails(string roomId, int attempts, int seed)
    {
        var hash = 2166136261u;
        foreach (var character in $"{roomId}:{attempts}:{seed}")
        {
            hash = (hash ^ character) * 16777619u;
        }

        return hash % 10 < 6;
    }
}
