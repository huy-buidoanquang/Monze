using Monze.Application;
using Monze.Domain;
using Npgsql;

namespace Monze.Infrastructure.Persistence;

public sealed partial class PostgresOutboxRepository
{
    public async Task<IReadOnlyList<UncertainOutbox>> ClaimUncertainOutboxAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var settle = new NpgsqlCommand("""
            -- A 'sending' lease that expired (crash, lost database after the ack)
            -- may already be on the channel: reconcile it instead of resending it.
            -- due_at is kept: the send started after it, so it bounds the search.
            UPDATE outbox_delivery
            SET status = 'uncertain',
                last_error = 'delivery-uncertain',
                locked_until = NULL,
                lease_token = NULL
            WHERE status = 'sending' AND locked_until < now();

            -- A row waiting on a parent held for an administrator is held too.
            UPDATE outbox_delivery AS child
            SET status = 'uncertain',
                last_error = 'dependency-held'
            FROM outbox_delivery AS parent
            WHERE child.depends_on_id = parent.id
              AND child.status = 'pending'
              AND parent.status = 'uncertain'
              AND parent.last_error IS DISTINCT FROM 'delivery-uncertain';
            """, connection))
        {
            await settle.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand("""
            WITH due AS (
              SELECT id, meeting_session_id
              FROM outbox_delivery
              WHERE status = 'uncertain'
                AND last_error = 'delivery-uncertain'
                AND due_at <= now()
                AND (locked_until IS NULL OR locked_until < now())
              ORDER BY id
              FOR UPDATE SKIP LOCKED
              LIMIT @limit
            )
            UPDATE outbox_delivery AS item
            SET locked_until = now() + interval '60 seconds',
                lease_token = md5(random()::text || clock_timestamp()::text)
            FROM due
            LEFT JOIN meeting_session AS reply_session
              ON reply_session.id = due.meeting_session_id
            WHERE item.id = due.id
            RETURNING item.clan_id, item.id, item.channel_id, item.kind, item.body, item.attempts,
                      item.external_message_id, item.lease_token, item.due_at,
                      item.mention_everyone, item.content_json,
                      item.meeting_session_id, item.reply_to_message_id,
                      reply_session.direct_agent, reply_session.voice_channel_id,
                      reply_session.voice_channel_label;
            """, connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 256));
        var rows = new List<UncertainOutbox>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new UncertainOutbox(
                reader.GetInt64(0),
                new DueOutbox(
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.GetString(7),
                    reader.GetFieldValue<DateTimeOffset>(8),
                    reader.GetBoolean(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetInt64(11),
                    reader.IsDBNull(12) ? null : reader.GetInt64(12),
                    reader.IsDBNull(13) ? null : reader.GetBoolean(13),
                    reader.IsDBNull(14) ? null : reader.GetInt64(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15))));
        }

        return rows;
    }

    public async Task RequeueUncertainOutboxAsync(long id, string leaseToken, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE outbox_delivery
            SET status = CASE WHEN attempts + 1 >= @max THEN 'uncertain' ELSE 'pending' END,
                attempts = attempts + 1,
                last_error = CASE WHEN attempts + 1 >= @max THEN 'reconcile-exhausted' ELSE 'reconciled-absent' END,
                due_at = now(),
                locked_until = NULL,
                lease_token = NULL
            WHERE id = @id AND lease_token = @lease AND status = 'uncertain';
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("lease", leaseToken);
        command.Parameters.AddWithValue("max", OutboxPolicy.MaxAttempts);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<long>> FindRecordedMessagesAsync(IReadOnlyList<long> messageIds, CancellationToken cancellationToken)
    {
        var recorded = new HashSet<long>();
        if (messageIds.Count == 0)
        {
            return recorded;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT external_message_id FROM outbox_delivery WHERE external_message_id = ANY(@ids);",
            connection);
        command.Parameters.AddWithValue("ids", messageIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            recorded.Add(reader.GetInt64(0));
        }

        return recorded;
    }
}
