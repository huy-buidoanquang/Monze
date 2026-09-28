using Monze.Application;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;
public sealed partial class PostgresCommunityRepository : ICommunityRepository
{
    public async Task<long> CreateEventAsync(long clanId, long channelId, long ownerId, string title, DateTimeOffset startsAt, int? capacity, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO community_event(clan_id, channel_id, owner_id, title, starts_at, capacity)
            VALUES (@clan, @channel, @owner, @title, @starts, @capacity)
            RETURNING id;
            """, connection, tx);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("starts", startsAt);
        command.Parameters.AddWithValue("capacity", (object?)capacity ?? DBNull.Value);
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        foreach (var (label, offset) in new (string Label, TimeSpan Offset)[]
        {
            ("24 giờ", TimeSpan.FromHours(-24)),
            ("1 giờ", TimeSpan.FromHours(-1)),
            ("10 phút", TimeSpan.FromMinutes(-10)),
            ("bắt đầu", TimeSpan.Zero)
        })
        {
            await EnqueueAtAsync(connection, tx, clanId, channelId, OutboxKind.Reminder, $"event:{id}:{label}", $"{title}: nhắc {label}.", startsAt + offset, cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return id;
    }

    private static async Task EnqueueAtAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long clanId, long channelId, OutboxKind kind, string dedupeKey, string body, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, due_at)
            VALUES (@clan, @channel, @kind, @key, @body, @due)
            ON CONFLICT (dedupe_key) DO NOTHING;
            """, connection, tx);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("kind", kind.ToString());
        command.Parameters.AddWithValue("key", dedupeKey);
        command.Parameters.AddWithValue("body", body);
        command.Parameters.AddWithValue("due", dueAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<EventJoinStatus> JoinEventAsync(long clanId, long userId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long eventId;
        int? capacity;
        var foundEvent = false;
        await using (var current = new NpgsqlCommand("""
            SELECT id, capacity
            FROM community_event
            WHERE clan_id = @clan AND status = 'open'
            ORDER BY id DESC
            LIMIT 1
            FOR UPDATE;
            """, connection, transaction))
        {
            current.Parameters.AddWithValue("clan", clanId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                foundEvent = true;
                eventId = reader.GetInt64(0);
                capacity = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            }
            else
            {
                eventId = 0;
                capacity = null;
            }
        }

        if (!foundEvent)
        {
            await transaction.RollbackAsync(cancellationToken);
            return EventJoinStatus.NoEvent;
        }

        await using (var existing = new NpgsqlCommand("""
            SELECT 1
            FROM signup_entry
            WHERE event_id = @event AND user_id = @user;
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("event", eventId);
            existing.Parameters.AddWithValue("user", userId);
            if (await existing.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return EventJoinStatus.AlreadyRegistered;
            }
        }

        var status = "confirmed";
        if (capacity is int limit)
        {
            await using var count = new NpgsqlCommand("""
                SELECT COUNT(*)
                FROM signup_entry
                WHERE event_id = @event AND status = 'confirmed';
                """, connection, transaction);
            count.Parameters.AddWithValue("event", eventId);
            var confirmed = (long)(await count.ExecuteScalarAsync(cancellationToken) ?? 0L);
            if (confirmed >= limit)
            {
                status = "waitlisted";
            }
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO signup_entry(event_id, user_id, status)
            VALUES (@event, @user, @status);
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("event", eventId);
            insert.Parameters.AddWithValue("user", userId);
            insert.Parameters.AddWithValue("status", status);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return status == "confirmed" ? EventJoinStatus.Confirmed : EventJoinStatus.Waitlisted;
    }

    public async Task<string?> RecapEventAsync(long clanId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT e.title,
                   e.capacity,
                   (SELECT COUNT(*) FROM signup_entry s WHERE s.event_id = e.id AND s.status = 'confirmed'),
                   (SELECT COUNT(*) FROM signup_entry s WHERE s.event_id = e.id AND s.status = 'waitlisted'),
                   (
                     SELECT ms.summary_text
                     FROM meeting_summary ms
                     JOIN meeting_session m ON m.id = ms.session_id
                     WHERE m.clan_id = e.clan_id AND m.status = 'posted'
                     ORDER BY m.id DESC
                     LIMIT 1
                   )
            FROM community_event e
            WHERE e.clan_id = @clan
            ORDER BY e.id DESC
            LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var title = reader.GetString(0);
        int? capacity = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        var confirmed = reader.GetInt64(2);
        var waitlisted = reader.GetInt64(3);
        var summary = reader.IsDBNull(4) ? "Chưa có bản ghi thoại." : reader.GetString(4);
        var capacityText = capacity is int limit ? $"{confirmed}/{limit}" : confirmed.ToString();
        return $"{title}\nĐăng ký: {capacityText}\nDanh sách chờ: {waitlisted}\n{summary}";
    }

}

