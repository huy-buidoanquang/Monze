using Monze.Application;
using Monze.Domain;
using Monze.Infrastructure.Persistence;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Repositories;

/// <summary>
/// Races from the campaign plan (rounds × concurrent workers). Each claim,
/// compare-and-set or lease has exactly one winner, every due row is claimed
/// exactly once, a stale completion has no effect, the AI budget admits
/// exactly floor(cap / tokens) requests, and only allowed status transitions
/// happen.
/// </summary>
public sealed class RepositoryConcurrencyTests
{
    private const long ClanBase = 2_104_288_434_238_600_000;
    private const long Owner = 2_104_288_434_238_600_900;

    [DbFact]
    [Req("REQ-WEL-001", "REQ-WEL-002")]
    [Covers("port:IWelcomeRepository.SetWelcomeConfigurationAsync")]
    public async Task Welcome_save_with_the_same_version_has_one_winner()
    {
        await using var db = await AuditedDatabase.CreateAsync("welcome_cas");
        var repository = new PostgresWelcomeRepository(db.DataSource);
        await RaceAsync(db, "welcome-cas", rounds: 200, workers: 16, async round =>
        {
            var clan = ClanBase + round;
            await db.ExecuteAsync("INSERT INTO clan_registry(clan_id, owner_id) VALUES (@clan, @owner);", ("clan", clan), ("owner", Owner));
            var versions = await Parallel(16, _ => repository.SetWelcomeConfigurationAsync(
                clan, Owner, true, "Chào", new WelcomeEmbedSettings(Title: "x"), CancellationToken.None, expectedVersion: 0));
            return (versions.Count(static version => version > 0), 1);
        });
    }

    [DbFact]
    [Req("REQ-WEL-003")]
    [Covers("port:IWelcomeRepository.TryClaimWelcomeAsync")]
    [Covers("table:welcome_delivery")]
    public async Task Welcome_delivery_claim_has_one_winner()
    {
        await using var db = await AuditedDatabase.CreateAsync("welcome_claim");
        var repository = new PostgresWelcomeRepository(db.DataSource);
        await RaceAsync(db, "welcome-delivery", rounds: 200, workers: 16, async round =>
        {
            var claims = await Parallel(16, _ => repository.TryClaimWelcomeAsync(ClanBase, 1_000 + round, CancellationToken.None));
            return (claims.Count(static won => won), 1);
        });
    }

    [DbFact]
    [Req("REQ-MTG-001", "REQ-MTG-006")]
    [Covers("port:IMeetingRepository.SuggestMeetingAsync")]
    [Covers("table:voice_claim")]
    public async Task Voice_suggestion_has_one_winner_per_room()
    {
        await using var db = await AuditedDatabase.CreateAsync("voice_claim");
        var repository = new PostgresMeetingRepository(db.DataSource);
        await RaceAsync(db, "voice-suggest", rounds: 200, workers: 16, async round =>
        {
            var clan = ClanBase + round;
            var sessions = await Parallel(16, worker => repository.CreateMeetingAsync(clan, 10, 100 + worker, null, CancellationToken.None));
            var won = 0;
            await System.Threading.Tasks.Parallel.ForEachAsync(sessions, async (session, _) =>
            {
                if (await repository.SuggestMeetingAsync(session, 77, DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None))
                {
                    Interlocked.Increment(ref won);
                }
            });
            return (won, 1);
        });
    }

    [DbFact]
    [Req("REQ-MTG-003", "REQ-MTG-006")]
    [Covers("port:IMeetingRepository.TryClaimInboxAsync")]
    public async Task Agent_inbox_claim_has_one_winner()
    {
        await using var db = await AuditedDatabase.CreateAsync("agent_inbox");
        var repository = new PostgresMeetingRepository(db.DataSource);
        await RaceAsync(db, "agent-inbox", rounds: 200, workers: 16, async round =>
        {
            var claims = await Parallel(16, _ => repository.TryClaimInboxAsync("agent", $"event:{round}", CancellationToken.None));
            return (claims.Count(static won => won), 1);
        });
    }

    [DbFact]
    [Req("REQ-MTG-004", "REQ-MTG-006")]
    [Covers("port:IMeetingRepository.ListPendingSummariesAsync")]
    public async Task Summary_lease_has_one_holder()
    {
        await using var db = await AuditedDatabase.CreateAsync("summary_lease");
        var repository = new PostgresMeetingRepository(db.DataSource);
        await RaceAsync(db, "summary-lease", rounds: 200, workers: 16, async round =>
        {
            await db.ExecuteAsync(
                "INSERT INTO meeting_session(clan_id, text_channel_id, voice_channel_id, requester_id, status, room_id) VALUES (@clan, 10, 77, 100, 'summary_pending', @room);",
                ("clan", ClanBase + round),
                ("room", $"room-{round}"));
            var leases = await Parallel(16, _ => repository.ListPendingSummariesAsync(1, CancellationToken.None));
            return (leases.Sum(batch => batch.Count(item => item.RoomId == $"room-{round}")), 1);
        });
    }

    [DbFact]
    [Req("REQ-MTG-002")]
    [Covers("port:ISchedulingRepository.ClaimDueMeetingSchedulesAsync")]
    public async Task Due_schedules_are_each_claimed_once()
    {
        await using var db = await AuditedDatabase.CreateAsync("schedules");
        var repository = new PostgresSchedulingRepository(db.DataSource);
        using var ledger = CaseLedger.Open("integration", "concurrency-schedules");
        const int rows = 500;
        for (var i = 0; i < rows; i++)
        {
            await repository.CreateMeetingScheduleAsync(ClanBase, 10, 100, $"Lịch {i}", MeetingScheduleKind.Daily, "09:00", "Asia/Ho_Chi_Minh", DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);
        }

        var claimed = new System.Collections.Concurrent.ConcurrentBag<long>();
        await Parallel(32, async _ =>
        {
            while (true)
            {
                var batch = await repository.ClaimDueMeetingSchedulesAsync(CancellationToken.None);
                if (batch.Count == 0)
                {
                    return 0;
                }

                foreach (var item in batch)
                {
                    claimed.Add(item.Id);
                }
            }
        });

        var outcome = claimed.Count == rows && claimed.Distinct().Count() == rows ? "pass" : "fail";
        ledger.Record(outcome, $"{rows} due schedules, 32 workers, {claimed.Count} claims, {claimed.Distinct().Count()} distinct");
        Assert.Equal(rows, claimed.Count);
        Assert.Equal(rows, claimed.Distinct().Count());
        await db.AssertTransitionsAsync();
    }

    [DbFact]
    [Req("REQ-OUT-001")]
    [Covers("port:IOutboxRepository.ClaimDueOutboxAsync")]
    [Covers("port:IOutboxRepository.CompleteOutboxAsync")]
    public async Task Due_outbox_rows_are_each_claimed_once_and_stale_completions_do_nothing()
    {
        await using var db = await AuditedDatabase.CreateAsync("outbox");
        var repository = new PostgresOutboxRepository(db.DataSource);
        using var ledger = CaseLedger.Open("integration", "concurrency-outbox");
        const int rows = 500;
        await db.ExecuteAsync(
            "INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body) SELECT @clan, 10, 'Announcement', 'race:' || g, 'b' FROM generate_series(1, @rows) g;",
            ("clan", ClanBase),
            ("rows", rows));
        var leases = new System.Collections.Concurrent.ConcurrentBag<DueOutbox>();
        await Parallel(32, async _ =>
        {
            while (true)
            {
                var batch = await repository.ClaimDueOutboxAsync(CancellationToken.None, ClanBase);
                if (batch.Count == 0)
                {
                    return 0;
                }

                foreach (var item in batch)
                {
                    leases.Add(item);
                }
            }
        });

        Assert.Equal(rows, leases.Count);
        Assert.Equal(rows, leases.Select(static lease => lease.Id).Distinct().Count());
        var first = leases.OrderBy(static lease => lease.Id).First();
        await repository.CompleteOutboxAsync(first.Id, "stale-lease", 1, failed: false, CancellationToken.None);
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM outbox_delivery WHERE id = @id AND status = 'sending' AND external_message_id IS NULL;", ("id", first.Id)));
        await System.Threading.Tasks.Parallel.ForEachAsync(leases, async (lease, _) =>
            await repository.CompleteOutboxAsync(lease.Id, lease.LeaseToken, lease.Id, failed: false, CancellationToken.None));
        Assert.Equal(rows, await db.CountAsync("SELECT count(*) FROM outbox_delivery WHERE clan_id = @clan AND status = 'sent';", ("clan", ClanBase)));
        ledger.Record("pass", $"{rows} due rows, 32 workers, each claimed and completed once");
        await db.AssertTransitionsAsync();
    }

    [DbFact]
    [Req("REQ-AI-001")]
    [Covers("port:IAiUsageRepository.ConsumeAiAsync")]
    [Covers("table:ai_usage")]
    public async Task Ai_budget_admits_exactly_what_fits()
    {
        await using var db = await AuditedDatabase.CreateAsync("ai_usage");
        var repository = new PostgresAiUsageRepository(db.DataSource);
        await RaceAsync(db, "ai-budget", rounds: 100, workers: 32, async round =>
        {
            var tokens = 1 + (round % 7);
            var cap = 10 + (round * 3);
            var results = await Parallel(32, _ => repository.ConsumeAiAsync(ClanBase, 5_000 + round, tokens, cap, CancellationToken.None));
            return (results.Count(static result => result.Allowed), Math.Min(32, cap / tokens));
        });
    }

    /// <summary>Regression for CAND-05: unanswered requests give their tokens back, never below zero.</summary>
    [DbFact]
    [Req("REQ-AI-001")]
    [Covers("port:IAiUsageRepository.RefundAiAsync")]
    public async Task Ai_refund_gives_tokens_back_without_going_below_zero()
    {
        await using var db = await AuditedDatabase.CreateAsync("ai_refund");
        var repository = new PostgresAiUsageRepository(db.DataSource);
        Assert.Equal((true, 8), await repository.ConsumeAiAsync(ClanBase, 77, 8, 10, CancellationToken.None));
        Assert.False((await repository.ConsumeAiAsync(ClanBase, 77, 5, 10, CancellationToken.None)).Allowed);

        await repository.RefundAiAsync(ClanBase, 77, 5, CancellationToken.None);
        Assert.Equal((true, 8), await repository.ConsumeAiAsync(ClanBase, 77, 5, 10, CancellationToken.None));
        await repository.RefundAiAsync(ClanBase, 77, 100, CancellationToken.None);
        await repository.RefundAiAsync(ClanBase, 78, 5, CancellationToken.None);

        Assert.Equal(0, await db.CountAsync("SELECT tokens FROM ai_usage WHERE clan_id = @clan AND user_id = 77;", ("clan", ClanBase)));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM ai_usage WHERE clan_id = @clan AND user_id = 78;", ("clan", ClanBase)));
        await db.AssertTransitionsAsync();
    }

    [DbFact]
    [Req("REQ-INBOX-001")]
    [Covers("port:ICommandInboxRepository.TryClaimAsync")]
    public async Task Command_inbox_claim_has_one_lease()
    {
        await using var db = await AuditedDatabase.CreateAsync("command_inbox");
        var repository = new PostgresCommandInboxRepository(db.DataSource);
        await RaceAsync(db, "command-inbox", rounds: 200, workers: 32, async round =>
        {
            var leases = await Parallel(32, _ => repository.TryClaimAsync(ClanBase, 10, 9_000 + round, CancellationToken.None));
            return (leases.Count(static lease => lease is not null), 1);
        });
    }

    [DbFact]
    [Req("REQ-INBOX-002")]
    [Covers("port:IInteractionInboxRepository.TryClaimAsync")]
    public async Task Interaction_inbox_claim_has_one_lease()
    {
        await using var db = await AuditedDatabase.CreateAsync("interaction_inbox");
        var repository = new PostgresInteractionInboxRepository(db.DataSource);
        await RaceAsync(db, "interaction-inbox", rounds: 200, workers: 32, async round =>
        {
            var leases = await Parallel(32, _ => repository.TryClaimAsync(ClanBase, 10, 9_000 + round, "monze_meeting_now", CancellationToken.None));
            return (leases.Count(static lease => lease is not null), 1);
        });
    }

    private static async Task RaceAsync(AuditedDatabase db, string name, int rounds, int workers, Func<int, Task<(int Actual, int Expected)>> round)
    {
        using var ledger = CaseLedger.Open("integration", "concurrency-" + name);
        var failures = new List<string>();
        for (var i = 0; i < rounds; i++)
        {
            var (actual, expected) = await round(i);
            var input = $"round {i}: {workers} workers";
            if (actual == expected)
            {
                ledger.Pass(input);
            }
            else
            {
                ledger.Fail(input, $"{actual} winners, expected {expected}");
                failures.Add($"round {i}: {actual} winners, expected {expected}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(10)));
        await db.AssertTransitionsAsync();
    }

    private static async Task<T[]> Parallel<T>(int workers, Func<int, Task<T>> work)
    {
        // All workers wait on one gate so they hit the database together
        // without blocking thread-pool threads.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
        {
            await gate.Task;
            return await work(worker);
        })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }
}
