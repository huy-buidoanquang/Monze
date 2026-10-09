using Npgsql;

namespace Monze.Testing.Harness;

/// <summary>
/// Database invariants that must hold whenever a run is quiescent (after a
/// drain, a fault has healed, or a restart has settled). Each one is a
/// counting query over the Monze schema; violations carry only an id and a
/// count, never row values. Uniqueness that a key or unique index already
/// guarantees (one welcome per user, one summary per room, one voice claim per
/// channel, one outbox row per dedupe key) is proven by the schema
/// fingerprint and is not repeated here.
/// </summary>
public static class MonzeInvariants
{
    public static IReadOnlyList<(string Id, string Description)> Catalog { get; } =
    [
        ("lease.outbox", "outbox_delivery stuck in 'sending' after its lease expired"),
        ("lease.schedule", "meeting_schedule stuck in 'running' after its lease expired"),
        ("lease.summary", "summary_pending meeting whose summary lease expired and was not reclaimed"),
        ("lease.command", "command_inbox stuck in 'processing' after its lease expired"),
        ("lease.interaction", "interaction_inbox stuck in 'processing' after its lease expired"),
        ("outbox.order", "outbox row sent before the row it depends on"),
        ("outbox.external-id", "one Mezon message id recorded by two outbox rows"),
        ("meeting.live-per-voice", "two live meetings in one voice channel"),
        ("voice.claim-holder", "unexpired voice claim held by a meeting that is not suggested or live"),
        ("scope.clan-zero", "row stored under clan_id 0"),
        ("scope.outbox-session", "outbox row and its meeting belong to different clans"),
        ("scope.context-root", "meeting cycle and its root meeting belong to different clans"),
        ("ai.cap", "AI usage above the daily token cap")
    ];

    private static readonly (string Id, string Sql)[] Queries =
    [
        ("lease.outbox", "SELECT count(*) FROM outbox_delivery WHERE status = 'sending' AND (locked_until IS NULL OR locked_until < now() - @grace);"),
        ("lease.schedule", "SELECT count(*) FROM meeting_schedule WHERE status = 'running' AND (locked_until IS NULL OR locked_until < now() - @grace);"),
        ("lease.summary", "SELECT count(*) FROM meeting_session WHERE status = 'summary_pending' AND summary_locked_until < now() - @grace;"),
        ("lease.command", "SELECT count(*) FROM command_inbox WHERE status = 'processing' AND locked_until < now() - @grace;"),
        ("lease.interaction", "SELECT count(*) FROM interaction_inbox WHERE status = 'processing' AND locked_until < now() - @grace;"),
        ("outbox.order", """
            SELECT count(*) FROM outbox_delivery child
            JOIN outbox_delivery parent ON parent.id = child.depends_on_id
            WHERE child.status = 'sent' AND parent.status <> 'sent';
            """),
        ("outbox.external-id", """
            SELECT count(*) FROM (
              SELECT external_message_id FROM outbox_delivery
              WHERE external_message_id IS NOT NULL
              GROUP BY external_message_id HAVING count(*) > 1) duplicate;
            """),
        ("meeting.live-per-voice", """
            SELECT count(*) FROM (
              SELECT clan_id, voice_channel_id FROM meeting_session
              WHERE status = 'live' AND voice_channel_id IS NOT NULL
              GROUP BY clan_id, voice_channel_id HAVING count(*) > 1) duplicate;
            """),
        ("voice.claim-holder", """
            SELECT count(*) FROM voice_claim claim
            LEFT JOIN meeting_session session ON session.id = claim.session_id
            WHERE claim.expires_at >= now()
              AND (session.id IS NULL OR session.status NOT IN ('suggested', 'live'));
            """),
        ("scope.outbox-session", """
            SELECT count(*) FROM outbox_delivery item
            JOIN meeting_session session ON session.id = item.meeting_session_id
            WHERE item.clan_id <> session.clan_id;
            """),
        ("scope.context-root", """
            SELECT count(*) FROM meeting_session cycle
            JOIN meeting_session root ON root.id = cycle.root_session_id
            WHERE cycle.clan_id <> root.clan_id;
            """)
    ];

    /// <summary>
    /// Checks every invariant. A lease counts as stuck once it expired more
    /// than <paramref name="leaseGrace"/> ago (the workers reclaim on their
    /// next poll); <paramref name="aiDailyTokenCap"/> enables ai.cap.
    /// </summary>
    public static async Task<IReadOnlyList<InvariantViolation>> CheckAsync(
        NpgsqlDataSource dataSource,
        TimeSpan leaseGrace,
        int? aiDailyTokenCap = null,
        CancellationToken cancellationToken = default)
    {
        var violations = new List<InvariantViolation>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        foreach (var (id, sql) in Queries)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            if (sql.Contains("@grace", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue("grace", leaseGrace);
            }

            Add(violations, id, await CountAsync(command, cancellationToken));
        }

        Add(violations, "scope.clan-zero", await ClanZeroRowsAsync(connection, cancellationToken));
        if (aiDailyTokenCap is { } cap)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM ai_usage WHERE tokens > @cap;", connection);
            command.Parameters.AddWithValue("cap", cap);
            Add(violations, "ai.cap", await CountAsync(command, cancellationToken));
        }

        return violations;
    }

    private static async Task<long> ClanZeroRowsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("""
            SELECT table_name FROM information_schema.columns
            WHERE table_schema = current_schema() AND column_name = 'clan_id'
            ORDER BY table_name;
            """, connection))
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        long total = 0;
        foreach (var table in tables)
        {
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WHERE clan_id = 0;", connection);
            total += await CountAsync(command, cancellationToken);
        }

        return total;
    }

    private static async Task<long> CountAsync(NpgsqlCommand command, CancellationToken cancellationToken)
        => Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);

    private static void Add(List<InvariantViolation> violations, string id, long count)
    {
        if (count > 0)
        {
            violations.Add(new InvariantViolation(id, Catalog.Single(entry => entry.Id == id).Description, count));
        }
    }
}
