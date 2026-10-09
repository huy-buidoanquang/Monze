using Monze.Testing;
using Monze.Testing.Harness;
using Monze.Testing.Postgres;
using Monze.Tests.Migrations;
using Npgsql;
using Xunit;

namespace Monze.Tests.Harness;

public sealed class MonzeInvariantsTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    [DbFact]
    [Req("REQ-HARN-003")]
    public async Task A_consistent_database_has_no_violations()
    {
        await using var database = await MigratedAsync("invariants_clean");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await ExecuteAsync(dataSource, """
            INSERT INTO clan_registry(clan_id, owner_id) VALUES (1, 5);
            -- A lease that expired less than the grace ago is still being reclaimed.
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, status, locked_until)
            VALUES (1, 10, 'Announcement', 'inv:recent', 'b', 'sending', now() - interval '30 seconds');
            INSERT INTO meeting_session(clan_id, text_channel_id, voice_channel_id, requester_id, status, room_id)
            VALUES (1, 10, 77, 5, 'live', 'inv-live');
            INSERT INTO voice_claim(clan_id, voice_channel_id, session_id, expires_at)
            SELECT 1, 77, id, 'infinity' FROM meeting_session WHERE room_id = 'inv-live';
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, status, external_message_id)
            VALUES (1, 10, 'MeetingSummary', 'inv:first', 'b', 'sent', 900);
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, status, external_message_id, depends_on_id)
            SELECT 1, 10, 'MeetingSummary', 'inv:second', 'b', 'sent', 901, id FROM outbox_delivery WHERE dedupe_key = 'inv:first';
            INSERT INTO ai_usage(clan_id, user_id, usage_day, tokens) VALUES (1, 5, current_date, 1000);
            """);

        Assert.Empty(await MonzeInvariants.CheckAsync(dataSource, Grace, aiDailyTokenCap: 1000));
    }

    [DbFact]
    [Req("REQ-HARN-003")]
    public async Task Every_invariant_reports_its_own_violation()
    {
        await using var database = await MigratedAsync("invariants_broken");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await ExecuteAsync(dataSource, """
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, status, locked_until)
            VALUES (1, 10, 'Announcement', 'inv:stuck', 'b', 'sending', now() - interval '10 minutes');
            INSERT INTO meeting_schedule(clan_id, channel_id, requester_id, kind, when_text, timezone, next_run_at, status, locked_until)
            VALUES (1, 10, 5, 'daily', '09:00', 'Asia/Ho_Chi_Minh', now(), 'running', now() - interval '10 minutes');
            INSERT INTO meeting_session(clan_id, text_channel_id, requester_id, status, room_id, summary_locked_until)
            VALUES (1, 10, 5, 'summary_pending', 'inv-summary', now() - interval '10 minutes');
            INSERT INTO command_inbox(clan_id, channel_id, message_id, status, lease_token, locked_until)
            VALUES (1, 10, 100, 'processing', 't', now() - interval '10 minutes');
            INSERT INTO interaction_inbox(clan_id, channel_id, message_id, action, status, lease_token, locked_until)
            VALUES (1, 10, 100, 'a', 'processing', 't', now() - interval '10 minutes');

            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, status)
            VALUES (1, 10, 'MeetingSummary', 'inv:parent', 'b', 'pending');
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, status, depends_on_id)
            SELECT 1, 10, 'MeetingSummary', 'inv:child', 'b', 'sent', id FROM outbox_delivery WHERE dedupe_key = 'inv:parent';
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, status, external_message_id)
            VALUES (1, 10, 'Announcement', 'inv:twice-1', 'b', 'sent', 999), (1, 10, 'Announcement', 'inv:twice-2', 'b', 'sent', 999);

            INSERT INTO meeting_session(clan_id, text_channel_id, voice_channel_id, requester_id, status, room_id)
            VALUES (1, 10, 77, 5, 'live', 'inv-live-1'), (1, 10, 77, 6, 'live', 'inv-live-2');
            INSERT INTO meeting_session(clan_id, text_channel_id, voice_channel_id, requester_id, status, room_id)
            VALUES (1, 10, 78, 5, 'posted', 'inv-posted');
            INSERT INTO voice_claim(clan_id, voice_channel_id, session_id, expires_at)
            SELECT 1, 78, id, now() + interval '1 hour' FROM meeting_session WHERE room_id = 'inv-posted';

            INSERT INTO clan_registry(clan_id, owner_id) VALUES (0, 5);
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, meeting_session_id)
            SELECT 2, 10, 'Announcement', 'inv:other-clan', 'b', id FROM meeting_session WHERE room_id = 'inv-posted';
            INSERT INTO meeting_session(clan_id, text_channel_id, requester_id, status, root_session_id)
            SELECT 2, 10, 5, 'posted', id FROM meeting_session WHERE room_id = 'inv-posted';
            INSERT INTO ai_usage(clan_id, user_id, usage_day, tokens) VALUES (1, 5, current_date, 1001);
            """);

        var violations = await MonzeInvariants.CheckAsync(dataSource, Grace, aiDailyTokenCap: 1000);

        Assert.Equal(MonzeInvariants.Catalog.Select(static entry => entry.Id).Order(), violations.Select(static violation => violation.Id).Order());
        Assert.All(violations, static violation => Assert.Equal(1, violation.Count));
    }

    private static async Task<CampaignDatabase> MigratedAsync(string tag)
    {
        var server = TestPostgres.ConnectionString;
        await MigrationTemplate.EnsureAsync(server);
        return await CampaignDatabase.CloneAsync(server, MigrationTemplate.Name, tag);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
