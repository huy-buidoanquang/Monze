using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;

public sealed partial class PostgresMeetingRepository
{
    public async Task<MeetingSessionBinding?> MarkMeetingEndedAsync(string roomId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var roomLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@room, 0));",
            connection,
            transaction))
        {
            roomLock.Parameters.AddWithValue("room", roomId);
            await roomLock.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand("""
            WITH target AS (
              SELECT id, status AS previous_status
              FROM meeting_session
              WHERE room_id = @room
              FOR UPDATE
            ), updated AS (
              UPDATE meeting_session AS session
              SET ended_at = COALESCE(session.ended_at, now()),
                  status = CASE
                      WHEN target.previous_status = 'live' THEN 'summary_pending'
                      ELSE session.status
                  END
              FROM target
              WHERE session.id = target.id
              RETURNING session.id, session.clan_id, session.text_channel_id,
                        session.voice_channel_id, session.direct_agent,
                        session.notification_message_id,
                        session.notification_channel_id,
                        session.meeting_title, session.voice_channel_label,
                        session.source_message_id, session.root_session_id,
                        target.previous_status
            )
            SELECT id, clan_id, text_channel_id, voice_channel_id, direct_agent,
                   notification_message_id, notification_channel_id,
                   meeting_title, voice_channel_label, source_message_id,
                   root_session_id, previous_status
            FROM updated;
            """, connection, transaction);
        command.Parameters.AddWithValue("room", roomId);
        MeetingSessionBinding? binding = null;
        var sessionFound = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                sessionFound = true;
                var previousStatus = reader.GetString(11);
                if (previousStatus is "live" or "summary_pending")
                {
                    binding = new MeetingSessionBinding(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetBoolean(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                        reader.IsDBNull(9) ? null : reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetInt64(10),
                        MeetingStatus.SummaryPending);
                }
            }
        }

        if (!sessionFound)
        {
            await RecordPendingAgentEventAsync(
                connection,
                transaction,
                roomId,
                "ended_pending",
                cancellationToken);
        }

        if (binding is not null)
        {
            await using var deleteClaim = new NpgsqlCommand("""
                DELETE FROM voice_claim
                WHERE clan_id = @clan AND voice_channel_id = @voice AND session_id = @session;
                """, connection, transaction);
            deleteClaim.Parameters.AddWithValue("clan", binding.ClanId);
            deleteClaim.Parameters.AddWithValue("voice", binding.VoiceChannelId);
            deleteClaim.Parameters.AddWithValue("session", binding.SessionId);
            await deleteClaim.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return binding;
    }

    public async Task<bool> StoreSummaryAsync(
        string roomId,
        string summary,
        string? transcript,
        CancellationToken cancellationToken,
        string? leaseToken = null)
        => await StoreSummaryAsync(
            roomId,
            summary,
            transcript,
            delivery: null,
            cancellationToken,
            leaseToken);

    public async Task<bool> StoreSummaryAsync(
        string roomId,
        string summary,
        string? transcript,
        MeetingSummaryDelivery? delivery,
        CancellationToken cancellationToken,
        string? leaseToken = null)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH updated AS (
              UPDATE meeting_session
              SET status = 'posted',
                  summary_attempts = 0,
                  summary_next_attempt_at = NULL,
                  summary_last_error = NULL,
                  summary_locked_until = NULL,
                  summary_lease_token = NULL
              WHERE room_id = @room
                AND status IN ('live', 'summary_pending', 'summary_failed')
                AND (@lease IS NULL OR summary_lease_token = @lease)
              RETURNING id, clan_id, text_channel_id, notification_channel_id,
                        notification_message_id, source_message_id
            ), saved AS (
              INSERT INTO meeting_summary(
                  session_id, summary_text, transcript, full_transcript, posted)
              SELECT id, @summary, NULL, @transcript, TRUE FROM updated
              ON CONFLICT (session_id) DO UPDATE
              SET summary_text = EXCLUDED.summary_text,
                  full_transcript = COALESCE(EXCLUDED.full_transcript, meeting_summary.full_transcript),
                  posted = TRUE
              RETURNING session_id
            ), first_outbox AS (
              INSERT INTO outbox_delivery(
                  clan_id, channel_id, kind, dedupe_key, body, content_json,
                  meeting_session_id, reply_to_message_id)
              SELECT clan_id,
                     text_channel_id,
                     'MeetingSummary',
                     'meeting:' || id::text,
                     @summary,
                     @summary_content,
                     id,
                     CASE WHEN notification_channel_id = text_channel_id
                          THEN COALESCE(source_message_id, notification_message_id)
                          ELSE NULL END
              FROM updated
              ON CONFLICT (dedupe_key) DO NOTHING
              RETURNING id
            ), first_delivery AS (
              SELECT id FROM first_outbox
              UNION ALL
              SELECT item.id
              FROM outbox_delivery AS item
              JOIN updated ON item.dedupe_key = 'meeting:' || updated.id::text
              WHERE NOT EXISTS (SELECT 1 FROM first_outbox)
            )
            INSERT INTO outbox_delivery(
                clan_id, channel_id, kind, dedupe_key, body, content_json,
                meeting_session_id, depends_on_id, reply_to_message_id)
            SELECT updated.clan_id,
                   updated.text_channel_id,
                   'MeetingSummary',
                   'meeting:' || updated.id::text || ':actions',
                   @summary,
                   @actions_content,
                   updated.id,
                   first_delivery.id,
                   NULL
            FROM updated
            CROSS JOIN first_delivery
            WHERE @actions_content IS NOT NULL
            ON CONFLICT (dedupe_key) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("summary", summary);
        command.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.Text)
        {
            Value = (object?)leaseToken ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("transcript", NpgsqlDbType.Jsonb)
        {
            Value = (object?)transcript ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("summary_content", NpgsqlDbType.Text)
        {
            Value = (object?)delivery?.SummaryContentJson ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("actions_content", NpgsqlDbType.Text)
        {
            Value = (object?)delivery?.ActionItemsContentJson ?? DBNull.Value
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var verify = new NpgsqlCommand(
            "SELECT status = 'posted' FROM meeting_session WHERE room_id = @room;",
            connection);
        verify.Parameters.AddWithValue("room", roomId);
        return (bool?)await verify.ExecuteScalarAsync(cancellationToken) ?? false;
    }

    public async Task MarkSummaryPendingAsync(string roomId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var roomLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@room, 0));",
            connection,
            transaction))
        {
            roomLock.Parameters.AddWithValue("room", roomId);
            await roomLock.ExecuteNonQueryAsync(cancellationToken);
        }

        string? status;
        await using (var current = new NpgsqlCommand(
            "SELECT status FROM meeting_session WHERE room_id = @room FOR UPDATE;",
            connection,
            transaction))
        {
            current.Parameters.AddWithValue("room", roomId);
            status = await current.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (status is null)
        {
            await RecordPendingAgentEventAsync(
                connection,
                transaction,
                roomId,
                "summary_ready_pending",
                cancellationToken);
        }
        else if (status is "live" or "summary_pending")
        {
            await using var command = new NpgsqlCommand("""
                UPDATE meeting_session
                SET status = 'summary_pending',
                    ended_at = COALESCE(ended_at, now()),
                    summary_next_attempt_at = COALESCE(summary_next_attempt_at, now()),
                    summary_last_error = NULL,
                    summary_locked_until = NULL,
                    summary_lease_token = NULL
                WHERE room_id = @room AND status IN ('live', 'summary_pending');
                """, connection, transaction);
            command.Parameters.AddWithValue("room", roomId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingMeetingSummary>> ListPendingSummariesAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = new List<PendingMeetingSummary>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH exhausted AS (
              UPDATE meeting_session
              SET status = 'summary_failed',
                  summary_next_attempt_at = NULL,
                  summary_locked_until = NULL,
                  summary_lease_token = NULL
              WHERE status = 'summary_pending'
                AND summary_attempts >= 8
              RETURNING id, clan_id, text_channel_id,
                        notification_channel_id, notification_message_id,
                        source_message_id
            ), failure_notice AS (
              INSERT INTO outbox_delivery(
                  clan_id, channel_id, kind, dedupe_key, body,
                  meeting_session_id, reply_to_message_id)
              SELECT clan_id,
                     text_channel_id,
                     'MeetingSummary',
                     'meeting-summary-failed:' || id::text,
                     @failure_message,
                     id,
                     CASE WHEN notification_channel_id = text_channel_id
                          THEN COALESCE(source_message_id, notification_message_id)
                          ELSE NULL END
              FROM exhausted
              ON CONFLICT (dedupe_key) DO NOTHING
              RETURNING id
            ), due AS (
              SELECT id
              FROM meeting_session
              WHERE status = 'summary_pending'
                AND room_id IS NOT NULL
                AND summary_attempts < 8
                AND (summary_next_attempt_at IS NULL OR summary_next_attempt_at <= now())
                AND (summary_locked_until IS NULL OR summary_locked_until < now())
              ORDER BY COALESCE(summary_next_attempt_at, now()), id
              FOR UPDATE SKIP LOCKED
              LIMIT @limit
            )
            UPDATE meeting_session AS item
            SET summary_locked_until = now() + interval '60 seconds',
                summary_lease_token = md5(random()::text || clock_timestamp()::text)
            FROM due
            CROSS JOIN (SELECT count(*) FROM failure_notice) AS finalized
            WHERE item.id = due.id
            RETURNING item.room_id, item.summary_attempts, item.summary_lease_token;
            """, connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 128));
        command.Parameters.AddWithValue("failure_message", MonzeMessages.SummaryFailed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PendingMeetingSummary(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
        }

        return rows;
    }

    public async Task RecordSummaryRetryAsync(
        string roomId,
        string error,
        CancellationToken cancellationToken,
        string? leaseToken = null)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH updated AS (
              UPDATE meeting_session
              SET summary_attempts = summary_attempts + 1,
                  status = CASE
                      WHEN summary_attempts + 1 >= 8 THEN 'summary_failed'
                      ELSE status
                  END,
                  summary_next_attempt_at = CASE
                      WHEN summary_attempts + 1 >= 8 THEN NULL
                      ELSE now() + (LEAST(300, 5 * power(2, LEAST(summary_attempts, 5))) * interval '1 second')
                  END,
                  summary_last_error = LEFT(@error, 1024),
                  summary_locked_until = NULL,
                  summary_lease_token = NULL
              WHERE room_id = @room
                AND status = 'summary_pending'
                AND (@lease IS NULL OR summary_lease_token = @lease)
              RETURNING id, clan_id, text_channel_id, summary_attempts,
                        notification_channel_id, notification_message_id,
                        source_message_id
            )
            INSERT INTO outbox_delivery(
                clan_id, channel_id, kind, dedupe_key, body,
                meeting_session_id, reply_to_message_id)
            SELECT clan_id,
                   text_channel_id,
                   'MeetingSummary',
                   'meeting-summary-failed:' || id::text,
                   @failure_message,
                   id,
                   CASE WHEN notification_channel_id = text_channel_id
                        THEN COALESCE(source_message_id, notification_message_id)
                        ELSE NULL END
            FROM updated
            WHERE summary_attempts >= 8
            ON CONFLICT (dedupe_key) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("failure_message", MonzeMessages.SummaryFailed);
        command.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.Text)
        {
            Value = (object?)leaseToken ?? DBNull.Value
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MeetingSummaryContext?> GetSummaryContextAsync(
        string roomId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT s.id, s.clan_id, s.voice_channel_id, s.text_channel_id,
                   s.room_id, s.meeting_title, s.voice_channel_label,
                   s.started_at, s.ended_at,
                   s.notification_message_id, s.notification_channel_id,
                   s.source_message_id
            FROM meeting_session s
            WHERE s.room_id = @room
            ORDER BY s.id DESC
            LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MeetingSummaryContext(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? roomId : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetInt64(11));
    }

    public async Task<MeetingSummaryRecord?> GetSummaryAsync(
        long clanId,
        long sessionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT s.id, s.voice_channel_id, s.text_channel_id, ms.summary_text,
                   s.started_at, s.ended_at, s.notification_message_id,
                   s.room_id, s.meeting_title, s.voice_channel_label,
                   ms.full_transcript, s.clan_id
            FROM meeting_summary ms
            JOIN meeting_session s ON s.id = ms.session_id
            WHERE s.clan_id = @clan AND s.id = @session AND ms.posted;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MeetingSummaryRecord(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetInt64(11),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private static async Task RecordPendingAgentEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string roomId,
        string eventType,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO agent_event(room_id, event_type)
            VALUES (@room, @event_type)
            ON CONFLICT (room_id, event_type) DO NOTHING;
            """, connection, transaction);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("event_type", eventType);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

}
