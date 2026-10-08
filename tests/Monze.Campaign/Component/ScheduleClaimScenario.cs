using System.Collections.Concurrent;
using System.Diagnostics;
using Monze.Infrastructure.Persistence;
using Monze.Testing.Harness;

namespace Monze.Campaign.Component;

/// <summary>
/// C-SCHED: eight scheduler workers claim and commit a backlog of due
/// one-off schedules, each in its own voice channel. Every schedule must
/// produce exactly one meeting and one announcement and end completed.
/// </summary>
public static class ScheduleClaimScenario
{
    private const int Workers = 8;

    public static async Task<IReadOnlyList<CampaignArtifact>> RunAsync(ComponentContext context)
    {
        var schedules = context.Full ? 10_000 : 2_000;
        var artifact = new CampaignArtifact("component", "C-SCHED", $"Scheduler: {schedules:N0} lịch due, {Workers} worker");
        var (database, dataSource) = await context.CreateDatabaseAsync("c_sched");
        await using (database)
        await using (dataSource)
        {
            await ComponentContext.ExecuteAsync(dataSource, $"""
                INSERT INTO meeting_schedule(clan_id, channel_id, requester_id, title, kind, when_text, timezone, next_run_at)
                SELECT (g % 1000) + 1, 10 + g % 10, 5, 'Lịch ' || g, 'Once', '09:00', 'Asia/Ho_Chi_Minh', now() - interval '1 minute'
                FROM generate_series(1, {schedules}) g;
                ANALYZE meeting_schedule;
                """);
            var scheduling = new PostgresSchedulingRepository(dataSource);
            var meetings = new PostgresScheduledMeetingRepository(dataSource);
            var committed = new ConcurrentDictionary<long, int>();
            var claims = new LatencyHistogram();
            var commits = new LatencyHistogram();
            long rejected = 0;
            long duplicates = 0;
            var watch = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
            {
                while (true)
                {
                    var started = Stopwatch.GetTimestamp();
                    var batch = await scheduling.ClaimDueMeetingSchedulesAsync(CancellationToken.None);
                    claims.Record(Stopwatch.GetElapsedTime(started));
                    if (batch.Count == 0)
                    {
                        return;
                    }

                    foreach (var schedule in batch)
                    {
                        started = Stopwatch.GetTimestamp();
                        var ok = await meetings.CommitScheduledMeetingAsync(
                            schedule,
                            voiceChannelId: 1_000_000 + schedule.Id,
                            claimUntil: DateTimeOffset.UtcNow.AddMinutes(10),
                            nextRunAt: null,
                            announcementBody: "b",
                            contentJson: "{}",
                            mentionEveryone: false,
                            CancellationToken.None);
                        commits.Record(Stopwatch.GetElapsedTime(started));
                        if (!ok)
                        {
                            Interlocked.Increment(ref rejected);
                        }
                        else if (!committed.TryAdd(schedule.Id, worker))
                        {
                            Interlocked.Increment(ref duplicates);
                        }
                    }
                }
            })));
            watch.Stop();

            var occurrences = await ComponentContext.CountAsync(dataSource, "SELECT count(*) FROM outbox_delivery WHERE dedupe_key LIKE 'meeting-schedule:%';");
            var sessions = await ComponentContext.CountAsync(dataSource, "SELECT count(*) FROM meeting_session WHERE status = 'suggested';");
            var completed = await ComponentContext.CountAsync(dataSource, "SELECT count(*) FROM meeting_schedule WHERE status = 'completed';");
            var violations = await MonzeInvariants.CheckAsync(dataSource, TimeSpan.FromMinutes(1));
            artifact.Metric("schedules", schedules)
                .Metric("schedulesPerSecond", schedules / watch.Elapsed.TotalSeconds)
                .Latency("claim", claims)
                .Latency("commit", commits)
                .Invariant("one-occurrence", $"{schedules:N0} lịch → {schedules:N0} meeting, {schedules:N0} announcement", $"{committed.Count:N0} commit, {duplicates:N0} trùng, {rejected:N0} bị từ chối, {sessions:N0} meeting, {occurrences:N0} announcement", committed.Count == schedules && duplicates == 0 && rejected == 0 && sessions == schedules && occurrences == schedules)
                .Invariant("all-completed", $"{schedules:N0} lịch completed", $"{completed:N0}", completed == schedules)
                .DbInvariants(violations)
                .P99AtMost("claim-p99", claims, 50, "SLO đề xuất DB p99")
                .P99AtMost("commit-p99", commits, 50, "SLO đề xuất DB p99");
        }

        return [artifact];
    }
}
