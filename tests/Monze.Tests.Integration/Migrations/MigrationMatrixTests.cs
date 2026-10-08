using Monze.Infrastructure.Persistence;
using Monze.Testing;
using Monze.Testing.Postgres;
using Npgsql;
using Xunit;

namespace Monze.Tests.Migrations;

/// <summary>
/// The schema reached from zero, from every split point and on a second
/// PostgreSQL major version is the same; data around the destructive and
/// backfilling migrations (018, 025, 026, 028) is kept or removed exactly as
/// the migration intends; re-runs and concurrent runs change nothing; a
/// tampered or missing bookkeeping row and an unmigrated database are refused.
/// </summary>
public sealed class MigrationMatrixTests
{
    private static string Server => TestPostgres.ConnectionString;

    [DbFact]
    [Req("REQ-DB-001")]
    [Covers("migration:028_agent_event_scope")]
    public async Task Schema_from_zero_validates_and_matches_the_template()
    {
        var template = await MigrationTemplate.EnsureAsync(Server);
        await using var database = await CampaignDatabase.CreateEmptyAsync(Server, "zero");

        await MigrationSteps.ApplyAllAsync(database.ConnectionString);

        await using (var dataSource = NpgsqlDataSource.Create(database.ConnectionString))
        {
            await PostgresMigrator.ValidateAsync(dataSource, CancellationToken.None);
        }

        var lines = await SchemaFingerprint.LinesAsync(database.ConnectionString);
        Assert.True(lines.SequenceEqual(template), SchemaFingerprint.Difference(template, lines));
        Assert.Equal(MigrationSteps.All.Count, await MigrationSteps.ScalarAsync<long>(database.ConnectionString, "SELECT count(*) FROM schema_migrations;"));
    }

    [DbFact]
    [Req("REQ-DB-001")]
    public async Task Every_split_point_reaches_the_template_schema()
    {
        var template = await MigrationTemplate.EnsureAsync(Server);
        using var ledger = CaseLedger.Open("integration", "migration-split-points");
        var failures = new List<string>();
        for (var applied = 0; applied <= MigrationSteps.All.Count; applied++)
        {
            await using var database = await CampaignDatabase.CreateEmptyAsync(Server, $"split{applied}");
            await MigrationSteps.ApplyFirstAsync(database.ConnectionString, applied);
            await MigrationSteps.ApplyAllAsync(database.ConnectionString);
            var lines = await SchemaFingerprint.LinesAsync(database.ConnectionString);
            var input = $"{applied} migrations applied first, then the migrator";
            if (lines.SequenceEqual(template))
            {
                ledger.Pass(input);
            }
            else
            {
                var difference = SchemaFingerprint.Difference(template, lines);
                ledger.Fail(input, difference);
                failures.Add($"{input}:{Environment.NewLine}{difference}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [AlternateDbFact]
    [Req("REQ-DB-001")]
    public async Task Second_postgres_version_migrates_from_zero_to_the_same_tables_and_columns()
    {
        var primary = await MigrationTemplate.EnsureAsync(Server);
        var alternate = await MigrationTemplate.EnsureAsync(TestPostgres.AlternateConnectionString);
        var version = await MigrationSteps.ScalarAsync<string>(TestPostgres.AlternateConnectionString, "SHOW server_version;");

        // Index and constraint text can differ in formatting between major
        // versions; tables, columns and sequences must not.
        static IEnumerable<string> Shape(IEnumerable<string> lines)
            => lines.Where(static line => line.StartsWith("table ", StringComparison.Ordinal)
                || line.StartsWith("column ", StringComparison.Ordinal)
                || line.StartsWith("sequence ", StringComparison.Ordinal));
        var expected = Shape(primary).ToList();
        var actual = Shape(alternate).ToList();
        Assert.True(expected.SequenceEqual(actual), $"server {version}:{Environment.NewLine}{SchemaFingerprint.Difference(expected, actual)}");
        Assert.Equal(primary.Count, alternate.Count);
    }

    [DbFact]
    [Req("REQ-DB-001", "REQ-ROLE-001", "REQ-OUT-001")]
    [Covers("migration:018_remove_community_features")]
    [Covers("migration:026_remove_retired_role_rules")]
    public async Task Community_removal_and_retired_rules_delete_only_their_rows()
    {
        await using var database = await CampaignDatabase.CreateEmptyAsync(Server, "m018");
        await MigrationSteps.ApplyFirstAsync(database.ConnectionString, MigrationSteps.IndexOf("018_"));
        await MigrationSteps.ExecuteAsync(database.ConnectionString, """
            INSERT INTO clan_registry(clan_id, owner_id) VALUES (101, 1);
            INSERT INTO role_rule(clan_id, role_id, rule_kind, enabled) VALUES
              (101, 1, 'min_points', TRUE), (101, 2, 'points', TRUE), (101, 3, 'on_join', TRUE),
              (101, 4, 'self_select', TRUE), (101, 5, 'existing_role', TRUE), (101, 6, 'tenure', TRUE);
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body) VALUES
              (101, 1, 'Reminder', 'reminder:1', 'b'), (101, 1, 'EventPost', 'event:1', 'b'),
              (101, 1, 'CommandReply', 'reply:1', 'b'), (101, 1, 'Announcement', 'announce:old', 'b'),
              (101, 1, 'Announcement', 'meeting:keep', 'b'), (101, 1, 'MeetingSummary', 'summary:keep', 'b');
            """);

        await MigrationSteps.ApplyAllAsync(database.ConnectionString);

        Assert.Equal(["on_join", "tenure"], await MigrationSteps.RowsAsync(database.ConnectionString, "SELECT rule_kind FROM role_rule WHERE clan_id = 101 ORDER BY role_id;"));
        Assert.Equal(["meeting:keep", "summary:keep"], await MigrationSteps.RowsAsync(database.ConnectionString, "SELECT dedupe_key FROM outbox_delivery WHERE clan_id = 101 ORDER BY dedupe_key;"));
        Assert.Empty(await MigrationSteps.RowsAsync(database.ConnectionString, """
            SELECT relname FROM pg_class
            WHERE relname IN ('signup_entry', 'community_event', 'knowledge_entry', 'activity_ledger', 'activity_balance', 'game_attempt', 'wheel_cooldown', 'topic_prompt');
            """));
    }

    [DbFact]
    [Req("REQ-DB-001", "REQ-MTG-006")]
    [Covers("migration:025_meeting_agent_cycles")]
    public async Task Agent_cycles_close_only_finished_meeting_contexts()
    {
        await using var database = await CampaignDatabase.CreateEmptyAsync(Server, "m025");
        await MigrationSteps.ApplyFirstAsync(database.ConnectionString, MigrationSteps.IndexOf("025_"));
        await MigrationSteps.ExecuteAsync(database.ConnectionString, """
            INSERT INTO meeting_session(clan_id, text_channel_id, voice_channel_id, requester_id, status, ended_at, created_at) VALUES
              (101, 1, 10, 1, 'requested', NULL, '2026-01-01T00:00:00Z'),
              (101, 1, 11, 1, 'suggested', NULL, '2026-01-01T00:00:00Z'),
              (101, 1, 12, 1, 'live', NULL, '2026-01-01T00:00:00Z'),
              (101, 1, 13, 1, 'posted', '2026-01-02T00:00:00Z', '2026-01-01T00:00:00Z'),
              (101, 1, 14, 1, 'expired', NULL, '2026-01-03T00:00:00Z');
            """);

        await MigrationSteps.ApplyAllAsync(database.ConnectionString);

        Assert.Equal(
            ["requested|null", "suggested|null", "live|null", "posted|2026-01-02T00:00:00Z", "expired|2026-01-03T00:00:00Z"],
            await MigrationSteps.RowsAsync(database.ConnectionString, """
                SELECT status, to_char(context_closed_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"')
                FROM meeting_session WHERE clan_id = 101 ORDER BY voice_channel_id;
                """));
    }

    [DbFact]
    [Req("REQ-DB-001", "REQ-MTG-003")]
    [Covers("table:agent_event")]
    [Covers("table:inbox_event")]
    public async Task Agent_event_scope_keeps_one_event_per_room_and_type()
    {
        await using var database = await CampaignDatabase.CreateEmptyAsync(Server, "m028");
        await MigrationSteps.ApplyFirstAsync(database.ConnectionString, MigrationSteps.IndexOf("028_"));
        await MigrationSteps.ExecuteAsync(database.ConnectionString, """
            INSERT INTO agent_event(room_id, event_type, created_at) VALUES
              ('old', 'session_started', now() - interval '2 days'),
              ('room', 'session_started', now() - interval '1 hour'),
              ('room', 'session_started', now() - interval '30 minutes'),
              ('room', 'session_ended', now());
            INSERT INTO inbox_event(source, event_key, received_at) VALUES
              ('agent', 'agent-old', now() - interval '2 days'),
              ('agent', 'agent-new', now()),
              ('command', 'command-old', now() - interval '2 days');
            """);
        var keeper = await MigrationSteps.ScalarAsync<long>(database.ConnectionString, "SELECT min(id) FROM agent_event WHERE room_id = 'room' AND event_type = 'session_started';");

        await MigrationSteps.ApplyAllAsync(database.ConnectionString);

        Assert.Equal(
            ["room|session_started|True", "room|session_ended|False"],
            await MigrationSteps.RowsAsync(database.ConnectionString, $"SELECT room_id, event_type, id = {keeper} FROM agent_event ORDER BY id;"));
        Assert.Equal(["agent|agent-new", "command|command-old"], await MigrationSteps.RowsAsync(database.ConnectionString, "SELECT source, event_key FROM inbox_event ORDER BY source, event_key;"));
        await Assert.ThrowsAsync<PostgresException>(() => MigrationSteps.ExecuteAsync(database.ConnectionString, "INSERT INTO agent_event(room_id, event_type) VALUES ('room', 'session_ended');"));
    }

    [DbFact]
    [Req("REQ-DB-001", "REQ-MIG-001")]
    public async Task Rerunning_and_concurrent_runs_change_nothing()
    {
        var template = await MigrationTemplate.EnsureAsync(Server);
        await using var database = await CampaignDatabase.CreateEmptyAsync(Server, "concurrent");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => MigrationSteps.ApplyAllAsync(database.ConnectionString))));
        var bookkeeping = await MigrationSteps.RowsAsync(database.ConnectionString, "SELECT version, checksum, applied_at FROM schema_migrations ORDER BY version;");
        await MigrationSteps.ApplyAllAsync(database.ConnectionString);

        Assert.Equal(MigrationSteps.All.Count, bookkeeping.Count);
        Assert.Equal(bookkeeping, await MigrationSteps.RowsAsync(database.ConnectionString, "SELECT version, checksum, applied_at FROM schema_migrations ORDER BY version;"));
        var lines = await SchemaFingerprint.LinesAsync(database.ConnectionString);
        Assert.True(lines.SequenceEqual(template), SchemaFingerprint.Difference(template, lines));
    }

    [DbTheory]
    [Req("REQ-DB-001", "REQ-HOST-008")]
    [InlineData("tampered-checksum")]
    [InlineData("missing-row")]
    [InlineData("unmigrated")]
    public async Task Validation_refuses_a_schema_that_does_not_match_the_migrations(string damage)
    {
        await MigrationTemplate.EnsureAsync(Server);
        await using var database = damage == "unmigrated"
            ? await CampaignDatabase.CreateEmptyAsync(Server, damage)
            : await CampaignDatabase.CloneAsync(Server, MigrationTemplate.Name, damage);
        var target = MigrationSteps.All[MigrationSteps.IndexOf("005_")].Version;
        if (damage == "tampered-checksum")
        {
            await MigrationSteps.ExecuteAsync(database.ConnectionString, $"UPDATE schema_migrations SET checksum = 'tampered' WHERE version = '{target}';");
        }
        else if (damage == "missing-row")
        {
            await MigrationSteps.ExecuteAsync(database.ConnectionString, $"DELETE FROM schema_migrations WHERE version = '{target}';");
        }

        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PostgresMigrator.ValidateAsync(dataSource, CancellationToken.None));

        Assert.Equal(
            damage == "unmigrated"
                ? "Monze schema is missing. Run 'Monze migrate' before starting the bot."
                : $"Migration {target} is missing or has a checksum mismatch.",
            error.Message);
        if (damage == "tampered-checksum")
        {
            var applyError = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationSteps.ApplyAllAsync(database.ConnectionString));
            Assert.Equal($"Migration {target} checksum changed after it was applied.", applyError.Message);
        }
    }

    [DbFact]
    [Req("REQ-DB-001")]
    public async Task Each_migration_reports_whether_it_can_run_twice()
    {
        // Applied migrations are immutable, so this records idempotency rather
        // than asserting it: a non-idempotent migration whose bookkeeping row
        // is lost cannot be re-applied by the migrator.
        await MigrationTemplate.EnsureAsync(Server);
        await using var database = await CampaignDatabase.CloneAsync(Server, MigrationTemplate.Name, "idempotency");
        using var ledger = CaseLedger.Open("integration", "migration-idempotency");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var (_, name, sql) in MigrationSteps.All)
        {
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                await command.ExecuteNonQueryAsync();
                ledger.Record("pass", name, new Dictionary<string, string> { ["rerun"] = "idempotent" });
            }
            catch (PostgresException ex)
            {
                ledger.Record("pass", name, new Dictionary<string, string> { ["rerun"] = "not-idempotent" }, note: ex.SqlState);
            }
            finally
            {
                await transaction.RollbackAsync();
            }
        }

        Assert.Equal(MigrationSteps.All.Count, ledger.Total);
    }
}
