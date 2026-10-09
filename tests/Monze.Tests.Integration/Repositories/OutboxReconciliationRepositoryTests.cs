using Monze.Domain;
using Monze.Infrastructure.Persistence;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Repositories;

/// <summary>
/// Regressions for DEF-07, CAND-21 and WF-02. An outbox row whose delivery is
/// uncertain is never resent blindly. An expired 'sending' lease (crash, lost
/// database after the ack) and an unacknowledged send are both reconciled
/// against the channel first (no sooner than 30 s after the send), then either
/// recorded as sent or requeued. A row waiting on a parent held for an
/// administrator is held too, with a reason.
/// </summary>
public sealed class OutboxReconciliationRepositoryTests
{
    private const long Clan = 2_104_288_434_238_700_401;

    [DbFact]
    [Req("REQ-OUT-001")]
    [Covers("port:IOutboxRepository.ClaimUncertainOutboxAsync", "port:IOutboxRepository.RequeueUncertainOutboxAsync")]
    public async Task An_expired_sending_lease_is_reconciled_instead_of_resent()
    {
        await using var db = await AuditedDatabase.CreateAsync("reconcile_expired");
        var repository = new PostgresOutboxRepository(db.DataSource);
        var id = await InsertAsync(db, "expired");
        var claimed = Assert.Single(await repository.ClaimDueOutboxAsync(CancellationToken.None, Clan));
        await db.ExecuteAsync("UPDATE outbox_delivery SET locked_until = now() - interval '1 second' WHERE id = @id;", ("id", id));

        Assert.Empty(await repository.ClaimDueOutboxAsync(CancellationToken.None, Clan));
        var uncertain = Assert.Single(await repository.ClaimUncertainOutboxAsync(10, CancellationToken.None));
        Assert.Equal(Clan, uncertain.ClanId);
        Assert.Equal(id, uncertain.Item.Id);
        Assert.NotEqual(claimed.LeaseToken, uncertain.Item.LeaseToken);

        // The due time the send started after bounds the channel search; a
        // settle long after the lease expired must not move it past the send.
        Assert.Equal(claimed.DueAt, uncertain.Item.DueAt);
        Assert.Empty(await repository.ClaimUncertainOutboxAsync(10, CancellationToken.None));

        await repository.RequeueUncertainOutboxAsync(id, uncertain.Item.LeaseToken, CancellationToken.None);
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM outbox_delivery WHERE id = @id AND status = 'pending' AND attempts = 1 AND last_error = 'reconciled-absent';", ("id", id)));
        Assert.Equal(id, Assert.Single(await repository.ClaimDueOutboxAsync(CancellationToken.None, Clan)).Id);
        await db.AssertTransitionsAsync();
    }

    [DbFact]
    [Req("REQ-OUT-001")]
    public async Task An_unacknowledged_send_waits_30_seconds_then_is_recorded_when_found()
    {
        await using var db = await AuditedDatabase.CreateAsync("reconcile_found");
        var repository = new PostgresOutboxRepository(db.DataSource);
        var id = await InsertAsync(db, "found");
        var claimed = Assert.Single(await repository.ClaimDueOutboxAsync(CancellationToken.None, Clan));
        await repository.CompleteOutboxAsync(id, claimed.LeaseToken, null, failed: true, CancellationToken.None, "delivery-uncertain");

        Assert.Empty(await repository.ClaimUncertainOutboxAsync(10, CancellationToken.None));
        await db.ExecuteAsync("UPDATE outbox_delivery SET due_at = now() - interval '1 second' WHERE id = @id;", ("id", id));
        var uncertain = Assert.Single(await repository.ClaimUncertainOutboxAsync(10, CancellationToken.None));

        await repository.CompleteOutboxAsync(id, uncertain.Item.LeaseToken, 1_900_000_000_000_000_001, failed: false, CancellationToken.None);
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM outbox_delivery WHERE id = @id AND status = 'sent' AND external_message_id = 1900000000000000001;", ("id", id)));
        Assert.Equal(
            new HashSet<long> { 1_900_000_000_000_000_001 },
            await repository.FindRecordedMessagesAsync([1_900_000_000_000_000_001, 1_900_000_000_000_000_002], CancellationToken.None));
        Assert.Empty(await repository.FindRecordedMessagesAsync([], CancellationToken.None));
        await db.AssertTransitionsAsync();
    }

    [DbFact]
    [Req("REQ-OUT-001")]
    public async Task Requeueing_stops_at_the_attempt_limit()
    {
        await using var db = await AuditedDatabase.CreateAsync("reconcile_limit");
        var repository = new PostgresOutboxRepository(db.DataSource);
        var id = await InsertAsync(db, "limit");
        var claimed = Assert.Single(await repository.ClaimDueOutboxAsync(CancellationToken.None, Clan));
        await repository.CompleteOutboxAsync(id, claimed.LeaseToken, null, failed: true, CancellationToken.None, "delivery-uncertain");
        await db.ExecuteAsync(
            "UPDATE outbox_delivery SET due_at = now() - interval '1 second', attempts = @attempts WHERE id = @id;",
            ("id", id),
            ("attempts", OutboxPolicy.MaxAttempts - 1));
        var uncertain = Assert.Single(await repository.ClaimUncertainOutboxAsync(10, CancellationToken.None));

        await repository.RequeueUncertainOutboxAsync(id, uncertain.Item.LeaseToken, CancellationToken.None);

        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM outbox_delivery WHERE id = @id AND status = 'uncertain' AND last_error = 'reconcile-exhausted';", ("id", id)));
        Assert.Empty(await repository.ClaimUncertainOutboxAsync(10, CancellationToken.None));
        Assert.Empty(await repository.ClaimDueOutboxAsync(CancellationToken.None, Clan));
        await db.AssertTransitionsAsync();
    }

    [DbFact]
    [Req("REQ-OUT-001")]
    public async Task A_row_waiting_on_a_held_parent_is_held_and_one_waiting_on_reconciliation_is_not()
    {
        await using var db = await AuditedDatabase.CreateAsync("reconcile_dependency");
        var repository = new PostgresOutboxRepository(db.DataSource);
        await db.ExecuteAsync("""
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, status, last_error, due_at)
            VALUES (@clan, 10, 'MeetingSummary', 'parent-held', 'b', 'uncertain', 'retry-limit', now() + interval '1 hour'),
                   (@clan, 10, 'MeetingSummary', 'parent-uncertain', 'b', 'uncertain', 'delivery-uncertain', now() + interval '1 hour');
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, depends_on_id)
            SELECT @clan, 10, 'MeetingSummary', 'child-of-' || dedupe_key, 'b', id FROM outbox_delivery WHERE dedupe_key LIKE 'parent-%';
            """,
            ("clan", Clan));

        Assert.Empty(await repository.ClaimUncertainOutboxAsync(10, CancellationToken.None));

        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM outbox_delivery WHERE dedupe_key = 'child-of-parent-held' AND status = 'uncertain' AND last_error = 'dependency-held';"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM outbox_delivery WHERE dedupe_key = 'child-of-parent-uncertain' AND status = 'pending';"));
        await db.AssertTransitionsAsync();
    }

    [DbFact]
    [Req("REQ-OUT-001")]
    [Covers("port:IOutboxRepository.CompleteOutboxAsync")]
    public async Task A_send_that_never_left_the_process_is_retried_without_using_an_attempt()
    {
        await using var db = await AuditedDatabase.CreateAsync("reconcile_not_sent");
        var repository = new PostgresOutboxRepository(db.DataSource);
        var id = await InsertAsync(db, "not-sent");
        for (var i = 0; i < OutboxPolicy.MaxAttempts + 2; i++)
        {
            await db.ExecuteAsync("UPDATE outbox_delivery SET due_at = now() WHERE id = @id;", ("id", id));
            var claimed = Assert.Single(await repository.ClaimDueOutboxAsync(CancellationToken.None, Clan));
            await repository.CompleteOutboxAsync(id, claimed.LeaseToken, null, failed: false, CancellationToken.None, "delivery-not-sent", countsAsAttempt: false);
        }

        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM outbox_delivery WHERE id = @id AND status = 'pending' AND attempts = 0 AND last_error = 'delivery-not-sent' AND due_at > now();", ("id", id)));
        await db.AssertTransitionsAsync();
    }

    /// <summary>
    /// A send waiting for upstream capacity keeps its row: the worker renews
    /// the leases of its deliveries, and only the current lease of a row that
    /// is still 'sending' is extended.
    /// </summary>
    [DbFact]
    [Req("REQ-OUT-001")]
    [Covers("port:IOutboxRepository.RenewOutboxLeasesAsync")]
    public async Task Only_the_current_lease_of_a_sending_row_is_renewed()
    {
        await using var db = await AuditedDatabase.CreateAsync("reconcile_renew");
        var repository = new PostgresOutboxRepository(db.DataSource);
        var waiting = await InsertAsync(db, "renew-waiting");
        var other = await InsertAsync(db, "renew-other");
        var claimed = (await repository.ClaimDueOutboxAsync(CancellationToken.None, Clan)).ToDictionary(static item => item.Id);
        await db.ExecuteAsync("UPDATE outbox_delivery SET locked_until = now() + interval '1 second';");
        await repository.CompleteOutboxAsync(other, claimed[other].LeaseToken, 1_900_000_000_000_000_011, failed: false, CancellationToken.None);

        await repository.RenewOutboxLeasesAsync(new Dictionary<long, string> { [waiting] = claimed[waiting].LeaseToken, [other] = claimed[other].LeaseToken }, CancellationToken.None);
        await repository.RenewOutboxLeasesAsync(new Dictionary<long, string>(), CancellationToken.None);

        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM outbox_delivery WHERE id = @id AND status = 'sending' AND locked_until > now() + interval '50 seconds';", ("id", waiting)));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM outbox_delivery WHERE id = @id AND status = 'sent' AND locked_until IS NULL;", ("id", other)));

        await db.ExecuteAsync("UPDATE outbox_delivery SET locked_until = now() + interval '1 second' WHERE id = @id;", ("id", waiting));
        await repository.RenewOutboxLeasesAsync(new Dictionary<long, string> { [waiting] = "stale" }, CancellationToken.None);
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM outbox_delivery WHERE id = @id AND locked_until < now() + interval '5 seconds';", ("id", waiting)));
        await db.AssertTransitionsAsync();
    }

    private static async Task<long> InsertAsync(AuditedDatabase db, string key)
    {
        await db.ExecuteAsync(
            "INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body) VALUES (@clan, 10, 'Announcement', @key, 'b');",
            ("clan", Clan),
            ("key", "reconcile:" + key));
        return await db.CountAsync("SELECT id FROM outbox_delivery WHERE dedupe_key = @key;", ("key", "reconcile:" + key));
    }
}
