using Monze.Application;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;
public sealed partial class PostgresOutboxRepository : IOutboxRepository
{
    public async Task<IReadOnlyList<DueOutbox>> ClaimDueOutboxAsync(
        CancellationToken cancellationToken,
        long? clanId = null)
    {
        var rows = new List<DueOutbox>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var sql = clanId is null ? """
            WITH due AS (
              SELECT id FROM outbox_delivery
              WHERE (status = 'pending' OR (status = 'sending' AND locked_until < now()))
                AND due_at <= now()
              ORDER BY id
              FOR UPDATE SKIP LOCKED
              LIMIT 256
            )
            UPDATE outbox_delivery AS item
            SET status = 'sending',
                locked_until = now() + interval '60 seconds',
                lease_token = md5(random()::text || clock_timestamp()::text)
            FROM due
            WHERE item.id = due.id
            RETURNING item.id, item.channel_id, item.kind, item.body, item.attempts,
                      item.external_message_id, item.lease_token, item.due_at,
                      item.mention_everyone, item.content_json,
                      item.meeting_session_id, item.reply_to_message_id;
            """ : """
            WITH due AS (
              SELECT id FROM outbox_delivery
              WHERE clan_id = @clan
                AND (status = 'pending' OR (status = 'sending' AND locked_until < now()))
                AND due_at <= now()
              ORDER BY id
              FOR UPDATE SKIP LOCKED
              LIMIT 256
            )
            UPDATE outbox_delivery AS item
            SET status = 'sending',
                locked_until = now() + interval '60 seconds',
                lease_token = md5(random()::text || clock_timestamp()::text)
            FROM due
            WHERE item.id = due.id
            RETURNING item.id, item.channel_id, item.kind, item.body, item.attempts,
                      item.external_message_id, item.lease_token, item.due_at,
                      item.mention_everyone, item.content_json,
                      item.meeting_session_id, item.reply_to_message_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
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
                reader.IsDBNull(11) ? null : reader.GetInt64(11)));
        }

        return rows;
    }

    public async Task CompleteOutboxAsync(
        long id,
        string leaseToken,
        long? externalMessageId,
        bool failed,
        CancellationToken cancellationToken,
        string? errorCode = null)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE outbox_delivery
            SET status = @status,
                attempts = attempts + 1,
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
                    ELSE due_at
                END
            WHERE id = @id AND lease_token = @lease;
            """, connection);
        var status = externalMessageId is not null ? "sent" : failed ? "uncertain" : "pending";
        command.Parameters.AddWithValue("status", status);
        command.Parameters.Add(new NpgsqlParameter("external", NpgsqlDbType.Bigint)
        {
            Value = (object?)externalMessageId ?? DBNull.Value
        });
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("lease", leaseToken);
        command.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text)
        {
            Value = (object?)errorCode ?? DBNull.Value
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

