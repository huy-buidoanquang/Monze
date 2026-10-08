using Monze.Application;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;
public sealed partial class PostgresOutboxRepository : IOutboxRepository
{
    public async Task<IReadOnlyList<DueOutbox>> ClaimDueOutboxAsync(
        CancellationToken cancellationToken,
        long? clanId = null,
        int limit = 256)
    {
        var rows = new List<DueOutbox>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var sql = clanId is null ? """
            WITH due AS (
              SELECT id, meeting_session_id FROM outbox_delivery
              WHERE status = 'pending'
                AND due_at <= now()
                AND (kind <> 'MeetingSummary' OR meeting_session_id IS NULL OR EXISTS (
                    SELECT 1 FROM meeting_session session
                    WHERE session.id = outbox_delivery.meeting_session_id
                      AND (session.direct_agent OR (
                          session.notification_channel_id = session.text_channel_id
                          AND COALESCE(session.source_message_id, session.notification_message_id) IS NOT NULL))))
                AND (depends_on_id IS NULL OR EXISTS (
                    SELECT 1 FROM outbox_delivery dependency
                    WHERE dependency.id = outbox_delivery.depends_on_id
                      AND dependency.status = 'sent'))
              ORDER BY id
              FOR UPDATE SKIP LOCKED
              LIMIT @limit
            )
            UPDATE outbox_delivery AS item
            SET status = 'sending',
                locked_until = now() + interval '60 seconds',
                lease_token = md5(random()::text || clock_timestamp()::text)
            FROM due
            LEFT JOIN meeting_session AS reply_session
              ON reply_session.id = due.meeting_session_id
            WHERE item.id = due.id
            RETURNING item.id, item.channel_id, item.kind, item.body, item.attempts,
                      item.external_message_id, item.lease_token, item.due_at,
                      item.mention_everyone, item.content_json,
                      item.meeting_session_id, item.reply_to_message_id,
                      reply_session.direct_agent, reply_session.voice_channel_id,
                      reply_session.voice_channel_label;
            """ : """
            WITH due AS (
              SELECT id, meeting_session_id FROM outbox_delivery
              WHERE clan_id = @clan
                AND status = 'pending'
                AND due_at <= now()
                AND (kind <> 'MeetingSummary' OR meeting_session_id IS NULL OR EXISTS (
                    SELECT 1 FROM meeting_session session
                    WHERE session.id = outbox_delivery.meeting_session_id
                      AND (session.direct_agent OR (
                          session.notification_channel_id = session.text_channel_id
                          AND COALESCE(session.source_message_id, session.notification_message_id) IS NOT NULL))))
                AND (depends_on_id IS NULL OR EXISTS (
                    SELECT 1 FROM outbox_delivery dependency
                    WHERE dependency.id = outbox_delivery.depends_on_id
                      AND dependency.status = 'sent'))
              ORDER BY id
              FOR UPDATE SKIP LOCKED
              LIMIT @limit
            )
            UPDATE outbox_delivery AS item
            SET status = 'sending',
                locked_until = now() + interval '60 seconds',
                lease_token = md5(random()::text || clock_timestamp()::text)
            FROM due
            LEFT JOIN meeting_session AS reply_session
              ON reply_session.id = due.meeting_session_id
            WHERE item.id = due.id
            RETURNING item.id, item.channel_id, item.kind, item.body, item.attempts,
                      item.external_message_id, item.lease_token, item.due_at,
                      item.mention_everyone, item.content_json,
                      item.meeting_session_id, item.reply_to_message_id,
                      reply_session.direct_agent, reply_session.voice_channel_id,
                      reply_session.voice_channel_label;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 256));
        if (clanId is not null)
        {
            command.Parameters.AddWithValue("clan", clanId.Value);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DueOutbox(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetBoolean(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                reader.IsDBNull(12) ? null : reader.GetBoolean(12),
                reader.IsDBNull(13) ? null : reader.GetInt64(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return rows;
    }

    public async Task CompleteOutboxAsync(
        long id,
        string leaseToken,
        long? externalMessageId,
        bool failed,
        CancellationToken cancellationToken,
        string? errorCode = null,
        bool countsAsAttempt = true)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH completed AS (
            UPDATE outbox_delivery
            SET status = @status,
                attempts = attempts + CASE WHEN @counts THEN 1 ELSE 0 END,
                external_message_id = COALESCE(@external, external_message_id),
                locked_until = NULL,
                lease_token = NULL,
                last_error = CASE
                    WHEN @status = 'sent' THEN NULL
                    WHEN @error IS NULL THEN last_error
                    ELSE LEFT(@error, 1024)
                END,
                due_at = CASE
                    WHEN @status = 'pending'
                    THEN now() + (power(2::numeric, LEAST(attempts + 1, 6)) * interval '5 seconds')
                    -- An uncertain delivery is reconciled against the channel no sooner than 30 s later.
                    WHEN @status = 'uncertain'
                    THEN now() + interval '30 seconds'
                    ELSE due_at
                END
            WHERE id = @id AND lease_token = @lease
            RETURNING kind, meeting_session_id, channel_id,
                      external_message_id, status
            ), target AS (
              SELECT COALESCE(session.root_session_id, session.id) AS root_id,
                     completed.channel_id,
                     completed.external_message_id AS message_id
              FROM completed
              JOIN meeting_session AS session
                ON session.id = completed.meeting_session_id
              WHERE completed.kind = 'Announcement'
                AND completed.status = 'sent'
                AND completed.external_message_id IS NOT NULL
            ), bound AS (
              UPDATE meeting_session AS item
              SET notification_channel_id = target.channel_id,
                  notification_message_id = target.message_id,
                  source_message_id = target.message_id
              FROM target
              WHERE item.id = target.root_id
                 OR item.root_session_id = target.root_id
              RETURNING item.id, item.notification_message_id AS message_id
            )
            UPDATE outbox_delivery AS delivery
            SET reply_to_message_id = bound.message_id
            FROM bound
            WHERE delivery.meeting_session_id = bound.id
              AND delivery.kind = 'MeetingSummary'
              AND delivery.depends_on_id IS NULL
              AND delivery.status IN ('pending', 'sending')
              AND delivery.external_message_id IS NULL;
            """, connection);
        var status = externalMessageId is not null ? "sent" : failed ? "uncertain" : "pending";
        command.Parameters.AddWithValue("status", status);
        command.Parameters.Add(new NpgsqlParameter("external", NpgsqlDbType.Bigint)
        {
            Value = (object?)externalMessageId ?? DBNull.Value
        });
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("lease", leaseToken);
        command.Parameters.AddWithValue("counts", countsAsAttempt);
        command.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text)
        {
            Value = (object?)errorCode ?? DBNull.Value
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

