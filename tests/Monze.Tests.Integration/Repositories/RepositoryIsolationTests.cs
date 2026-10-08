using Monze.Application;
using Monze.Domain;
using Monze.Infrastructure.Persistence;
using Monze.Testing;
using Npgsql;
using Xunit;

namespace Monze.Tests.Repositories;

/// <summary>
/// Claims skip rows another transaction holds (deterministic SKIP LOCKED
/// proof); every read is scoped to its clan and clan 0 never matches; and a
/// rejected voice suggestion leaves the winning meeting untouched.
/// Known gap CAND-15: a suggestion that loses the voice claim first closes the
/// open context of the meeting that holds the room, then cancels itself.
/// </summary>
public sealed class RepositoryIsolationTests
{
    private const long ClanA = 2_104_288_434_238_700_001;
    private const long ClanB = 2_104_288_434_238_700_002;
    private const long OwnerA = 2_104_288_434_238_700_101;
    private const long OwnerB = 2_104_288_434_238_700_102;

    [DbFact]
    [Req("REQ-OUT-001")]
    public async Task Outbox_claim_skips_rows_locked_by_another_transaction()
    {
        await using var db = await AuditedDatabase.CreateAsync("skip_outbox");
        var repository = new PostgresOutboxRepository(db.DataSource);
        await db.ExecuteAsync(
            "INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body) SELECT @clan, 10, 'Announcement', 'skip:' || g, 'b' FROM generate_series(1, 10) g;",
            ("clan", ClanA));
        var ids = await IdsAsync(db, "SELECT id FROM outbox_delivery WHERE clan_id = @clan ORDER BY id;", ClanA);
        var locked = ids.Take(3).ToArray();

        await using (var holder = await db.DataSource.OpenConnectionAsync())
        await using (var transaction = await holder.BeginTransactionAsync())
        {
            await using var lockRows = new NpgsqlCommand("SELECT id FROM outbox_delivery WHERE id = ANY(@ids) FOR UPDATE;", holder, transaction);
            lockRows.Parameters.AddWithValue("ids", locked);
            await lockRows.ExecuteNonQueryAsync();

            var claimed = (await repository.ClaimDueOutboxAsync(CancellationToken.None, ClanA)).Select(static item => item.Id).Order().ToArray();
            Assert.Equal(ids.Skip(3), claimed);
            await transaction.RollbackAsync();
        }

        Assert.Equal(locked, (await repository.ClaimDueOutboxAsync(CancellationToken.None, ClanA)).Select(static item => item.Id).Order());
        await db.AssertTransitionsAsync();
    }

    [DbFact]
    [Req("REQ-MTG-002")]
    public async Task Schedule_claim_skips_rows_locked_by_another_transaction()
    {
        await using var db = await AuditedDatabase.CreateAsync("skip_schedule");
        var repository = new PostgresSchedulingRepository(db.DataSource);
        for (var i = 0; i < 6; i++)
        {
            await repository.CreateMeetingScheduleAsync(ClanA, 10, OwnerA, $"Lịch {i}", MeetingScheduleKind.Daily, "09:00", "Asia/Ho_Chi_Minh", DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);
        }

        var ids = await IdsAsync(db, "SELECT id FROM meeting_schedule WHERE clan_id = @clan ORDER BY id;", ClanA);
        await using (var holder = await db.DataSource.OpenConnectionAsync())
        await using (var transaction = await holder.BeginTransactionAsync())
        {
            await using var lockRows = new NpgsqlCommand("SELECT id FROM meeting_schedule WHERE id = ANY(@ids) FOR UPDATE;", holder, transaction);
            lockRows.Parameters.AddWithValue("ids", ids.Take(2).ToArray());
            await lockRows.ExecuteNonQueryAsync();

            var claimed = (await repository.ClaimDueMeetingSchedulesAsync(CancellationToken.None)).Select(static item => item.Id).Order().ToArray();
            Assert.Equal(ids.Skip(2), claimed);
            await transaction.RollbackAsync();
        }

        Assert.Equal(ids.Take(2), (await repository.ClaimDueMeetingSchedulesAsync(CancellationToken.None)).Select(static item => item.Id).Order());
        await db.AssertTransitionsAsync();
    }

    [DbFact]
    [Req("REQ-SETUP-001", "REQ-WEL-001", "REQ-MTG-002", "REQ-MTG-004", "REQ-AI-001", "REQ-ING-001", "REQ-ROLE-001")]
    [Covers("port:IAuthorizationRepository.IsOwnerAsync")]
    [Covers("port:IAuthorizationRepository.IsAdminAsync")]
    [Covers("port:IWelcomeRepository.GetWelcomeAsync")]
    [Covers("port:ISchedulingRepository.ListMeetingSchedulesAsync")]
    [Covers("port:ISchedulingRepository.CancelMeetingScheduleAsync")]
    [Covers("port:IMeetingRepository.GetSummaryAsync")]
    [Covers("port:IRoleRepository.ListEnabledRoleRulesAsync")]
    [Covers("port:IMessageHistoryRepository.ChannelHasGapAsync")]
    public async Task Reads_are_scoped_to_their_clan_and_clan_zero_matches_nothing()
    {
        await using var db = await AuditedDatabase.CreateAsync("clan_scope");
        await db.ExecuteAsync("INSERT INTO clan_registry(clan_id, owner_id) VALUES (@a, @oa), (@b, @ob);", ("a", ClanA), ("oa", OwnerA), ("b", ClanB), ("ob", OwnerB));
        var authorization = new PostgresAuthorizationRepository(db.DataSource);
        var welcome = new PostgresWelcomeRepository(db.DataSource);
        var scheduling = new PostgresSchedulingRepository(db.DataSource);
        var meeting = new PostgresMeetingRepository(db.DataSource);
        var roles = new PostgresRoleRepository(db.DataSource);
        var history = new PostgresMessageHistoryRepository(db.DataSource);

        Assert.True(await authorization.IsOwnerAsync(ClanA, OwnerA, CancellationToken.None));
        Assert.False(await authorization.IsOwnerAsync(ClanB, OwnerA, CancellationToken.None));
        Assert.False(await authorization.IsAdminAsync(ClanB, OwnerA, CancellationToken.None));
        Assert.False(await authorization.IsOwnerAsync(0, OwnerA, CancellationToken.None));
        Assert.False(await authorization.IsAdminAsync(0, OwnerA, CancellationToken.None));
        Assert.True(await authorization.SetDelegateAsync(ClanA, OwnerA, OwnerB, true, CancellationToken.None));
        Assert.True(await authorization.IsAdminAsync(ClanA, OwnerB, CancellationToken.None));
        Assert.False(await authorization.SetDelegateAsync(ClanB, OwnerA, OwnerA, true, CancellationToken.None));

        Assert.True(await welcome.SetWelcomeAsync(ClanA, OwnerA, true, "A", CancellationToken.None) > 0);
        Assert.Equal(0, await welcome.SetWelcomeAsync(ClanB, OwnerA, true, "intruder", CancellationToken.None));
        Assert.Equal("A", (await welcome.GetWelcomeAsync(ClanA, CancellationToken.None))?.Text);
        Assert.NotEqual("intruder", (await welcome.GetWelcomeAsync(ClanB, CancellationToken.None))?.Text);
        Assert.Null(await welcome.GetWelcomeAsync(0, CancellationToken.None));

        var scheduleB = await scheduling.CreateMeetingScheduleAsync(ClanB, 10, OwnerB, "B", MeetingScheduleKind.Daily, "09:00", "Asia/Ho_Chi_Minh", DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        Assert.Empty(await scheduling.ListMeetingSchedulesAsync(ClanA, 10, OwnerA, 20, CancellationToken.None));
        Assert.False(await scheduling.CancelMeetingScheduleAsync(ClanA, 10, OwnerA, scheduleB, CancellationToken.None));
        Assert.Empty(await scheduling.ListMeetingSchedulesAsync(0, 10, OwnerB, 20, CancellationToken.None));
        Assert.Single(await scheduling.ListMeetingSchedulesAsync(ClanB, 10, OwnerB, 20, CancellationToken.None));

        await db.ExecuteAsync(
            "INSERT INTO meeting_session(clan_id, text_channel_id, voice_channel_id, requester_id, status, room_id) VALUES (@b, 10, 77, @ob, 'posted', 'room-b');",
            ("b", ClanB),
            ("ob", OwnerB));
        var sessionB = await db.CountAsync("SELECT id FROM meeting_session WHERE room_id = 'room-b';");
        await db.ExecuteAsync("INSERT INTO meeting_summary(session_id, summary_text) VALUES (@id, 'tóm tắt B');", ("id", sessionB));
        Assert.Null(await meeting.GetSummaryAsync(ClanA, sessionB, CancellationToken.None));
        Assert.Null(await meeting.GetSummaryAsync(0, sessionB, CancellationToken.None));

        Assert.True(await roles.SetRoleRuleAsync(ClanA, OwnerA, 7, RoleRuleKind.OnJoin, null, CancellationToken.None));
        Assert.Empty(await roles.ListEnabledRoleRulesAsync(ClanB, CancellationToken.None));
        Assert.Empty(await roles.ListEnabledRoleRulesAsync(0, CancellationToken.None));

        await db.ExecuteAsync("INSERT INTO channel_policy(clan_id, channel_id, persist_messages) VALUES (@a, 10, TRUE), (@b, 10, TRUE);", ("a", ClanA), ("b", ClanB));
        await history.MarkChannelGapAsync(ClanA, 10, 55, CancellationToken.None);
        Assert.True(await history.ChannelHasGapAsync(ClanA, 10, CancellationToken.None));
        Assert.False(await history.ChannelHasGapAsync(ClanB, 10, CancellationToken.None));
        Assert.False(await history.ChannelHasGapAsync(0, 10, CancellationToken.None));
        await db.AssertTransitionsAsync();
    }

    [DbFact]
    [Req("REQ-MTG-001", "REQ-MTG-006")]
    [Covers("port:IMeetingRepository.CreateMeetingAsync")]
    public async Task A_rejected_suggestion_leaves_the_winning_meeting_untouched()
    {
        await using var db = await AuditedDatabase.CreateAsync("voice_conflict");
        var repository = new PostgresMeetingRepository(db.DataSource);
        var first = await repository.CreateMeetingAsync(ClanA, 10, OwnerA, null, CancellationToken.None);
        Assert.True(await repository.SuggestMeetingAsync(first, 77, DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None));
        var second = await repository.CreateMeetingAsync(ClanA, 10, OwnerB, null, CancellationToken.None);

        Assert.False(await repository.SuggestMeetingAsync(second, 77, DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None));

        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM meeting_session WHERE id = @id AND status = 'cancelled';", ("id", second)));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM voice_claim WHERE clan_id = @clan AND voice_channel_id = 77 AND session_id = @id;", ("clan", ClanA), ("id", first)));
        await KnownDefect.ExpectFailureAsync("CAND-15", async () =>
            Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM meeting_session WHERE id = @id AND context_closed_at IS NOT NULL;", ("id", first))));
        await db.AssertTransitionsAsync();
    }

    private static async Task<long[]> IdsAsync(AuditedDatabase db, string sql, long clan)
    {
        await using var command = db.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("clan", clan);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<long>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids.ToArray();
    }
}
