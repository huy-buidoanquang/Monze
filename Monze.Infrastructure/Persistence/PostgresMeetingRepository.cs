using Monze.Application;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;
public sealed class PostgresMeetingRepository : IMeetingRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresMeetingRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<long> CreateMeetingAsync(long clanId, long channelId, long userId, long? eventId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO meeting_session(clan_id, text_channel_id, requester_id, status)
            VALUES (@clan, @channel, @user, 'requested')
            RETURNING id;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("user", userId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }


    public async Task<bool> SuggestMeetingAsync(long sessionId, long voiceChannelId, DateTimeOffset claimUntil, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var session = new NpgsqlCommand("""
            UPDATE meeting_session
            SET status = 'suggested', voice_channel_id = @voice, claim_until = @until
            WHERE id = @id AND status = 'requested';
            """, connection, tx);
        session.Parameters.AddWithValue("id", sessionId);
        session.Parameters.AddWithValue("voice", voiceChannelId);
        session.Parameters.AddWithValue("until", claimUntil);
        if (await session.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await tx.RollbackAsync(cancellationToken);
            return false;
        }
        await using (var expirePrevious = new NpgsqlCommand("""
            UPDATE meeting_session AS previous
            SET status = 'expired', claim_until = NULL
            WHERE previous.id = (
                SELECT claim.session_id
                FROM voice_claim claim
                JOIN meeting_session current_session
                  ON current_session.id = @id
                WHERE claim.clan_id = current_session.clan_id
                  AND claim.voice_channel_id = @voice
                  AND claim.expires_at < now()
            )
              AND previous.status = 'suggested';
            """, connection, tx))
        {
            expirePrevious.Parameters.AddWithValue("id", sessionId);
            expirePrevious.Parameters.AddWithValue("voice", voiceChannelId);
            await expirePrevious.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var claim = new NpgsqlCommand("""
            INSERT INTO voice_claim(clan_id, voice_channel_id, session_id, expires_at)
            SELECT clan_id, @voice, @id, @until FROM meeting_session WHERE id = @id
            ON CONFLICT (clan_id, voice_channel_id)
            DO UPDATE SET session_id = EXCLUDED.session_id, expires_at = EXCLUDED.expires_at
            WHERE voice_claim.expires_at < now() OR voice_claim.session_id = EXCLUDED.session_id;
            """, connection, tx);
        claim.Parameters.AddWithValue("id", sessionId);
        claim.Parameters.AddWithValue("voice", voiceChannelId);
        claim.Parameters.AddWithValue("until", claimUntil);
        if (await claim.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await using var revert = new NpgsqlCommand("""
                UPDATE meeting_session
                SET status = 'cancelled', voice_channel_id = NULL, claim_until = NULL
                WHERE id = @id;
                """, connection, tx);
            revert.Parameters.AddWithValue("id", sessionId);
            await revert.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return false;
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> BindMeetingRoomAsync(long clanId, long voiceChannelId, string roomId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(PostgresMeetingQueries.BindMeetingRoom, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("voice", voiceChannelId);
        command.Parameters.AddWithValue("room", roomId);
        var updated = await command.ExecuteScalarAsync(cancellationToken) is not null;
        if (!updated)
        {
            await using var log = new NpgsqlCommand("INSERT INTO agent_event(room_id, event_type) VALUES (@room, 'unmatched');", connection);
            log.Parameters.AddWithValue("room", roomId);
            await log.ExecuteNonQueryAsync(cancellationToken);
        }

        return updated;
    }

    public async Task<bool> BindMeetingRoomByVoiceChannelAsync(
        long voiceChannelId,
        string roomId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(PostgresMeetingQueries.BindMeetingRoomByVoiceChannel, connection);
        command.Parameters.AddWithValue("voice", voiceChannelId);
        command.Parameters.AddWithValue("room", roomId);
        var updated = await command.ExecuteScalarAsync(cancellationToken) is not null;
        if (!updated)
        {
            await RecordUnmatchedAgentEventAsync(connection, roomId, cancellationToken);
        }

        return updated;
    }

    public async Task<bool> TryClaimInboxAsync(
        string source,
        string eventKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO inbox_event(source, event_key)
            VALUES (@source, @key)
            ON CONFLICT (source, event_key) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("key", eventKey);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task ReleaseInboxAsync(
        string source,
        string eventKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM inbox_event
            WHERE source = @source AND event_key = @key;
            """, connection);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("key", eventKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PurgeInboxAsync(
        DateTimeOffset before,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM inbox_event WHERE received_at < @before;",
            connection);
        command.Parameters.AddWithValue("before", before);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkMeetingEndedAsync(string roomId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH ended AS (
              UPDATE meeting_session
              SET ended_at = COALESCE(ended_at, now()),
                  status = CASE WHEN status = 'live' THEN 'summary_pending' ELSE status END
              WHERE room_id = @room
              RETURNING clan_id, voice_channel_id
            )
            DELETE FROM voice_claim claim
            USING ended
            WHERE claim.clan_id = ended.clan_id
              AND claim.voice_channel_id = ended.voice_channel_id;
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> StoreSummaryAsync(
        string roomId,
        string summary,
        string? transcript,
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
                AND status IN ('live', 'summary_pending')
                AND (@lease IS NULL OR summary_lease_token = @lease)
              RETURNING id, clan_id, text_channel_id
            ), saved AS (
              INSERT INTO meeting_summary(session_id, summary_text, transcript, posted)
              SELECT id, @summary, @transcript, TRUE FROM updated
              ON CONFLICT (session_id) DO NOTHING
              RETURNING session_id
            )
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body)
            SELECT clan_id, text_channel_id, 'MeetingSummary', 'meeting:' || id::text, @summary
            FROM updated
            ON CONFLICT (dedupe_key) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("summary", summary);
        command.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.Text)
        {
            Value = (object?)leaseToken ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("transcript", NpgsqlDbType.Text)
        {
            Value = (object?)transcript ?? DBNull.Value
        });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task MarkSummaryPendingAsync(string roomId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE meeting_session
            SET status = 'summary_pending',
                summary_next_attempt_at = COALESCE(summary_next_attempt_at, now()),
                summary_last_error = NULL,
                summary_locked_until = NULL,
                summary_lease_token = NULL
            WHERE room_id = @room AND status IN ('live', 'summary_pending');
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingMeetingSummary>> ListPendingSummariesAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = new List<PendingMeetingSummary>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH due AS (
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
            WHERE item.id = due.id
            RETURNING item.room_id, item.summary_attempts, item.summary_lease_token;
            """, connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 128));
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
            UPDATE meeting_session
            SET summary_attempts = summary_attempts + 1,
                summary_next_attempt_at = CASE
                    WHEN summary_attempts + 1 >= 8 THEN NULL
                    ELSE now() + (LEAST(300, 5 * power(2, LEAST(summary_attempts, 5))) * interval '1 second')
                END,
                summary_last_error = LEFT(@error, 1024),
                summary_locked_until = NULL,
                summary_lease_token = NULL
            WHERE room_id = @room
              AND status = 'summary_pending'
              AND (@lease IS NULL OR summary_lease_token = @lease);
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.Text)
        {
            Value = (object?)leaseToken ?? DBNull.Value
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> LatestPostedSummaryAsync(long clanId, long voiceChannelId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT ms.summary_text
            FROM meeting_summary ms
            JOIN meeting_session s ON s.id = ms.session_id
            WHERE s.clan_id = @clan AND s.voice_channel_id = @voice AND s.status = 'posted'
            ORDER BY s.id DESC
            LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("voice", voiceChannelId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task ExpireSuggestedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE meeting_session SET status = 'expired'
            WHERE status = 'suggested' AND claim_until IS NOT NULL AND claim_until < @now;
            UPDATE meeting_session SET status = 'cancelled'
            WHERE status = 'requested' AND created_at < @requested_cutoff;
            DELETE FROM voice_claim
            WHERE expires_at < @now;
            """, connection);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("requested_cutoff", now.AddMinutes(-5));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RecordUnmatchedAgentEventAsync(
        NpgsqlConnection connection,
        string roomId,
        CancellationToken cancellationToken)
    {
        await using var log = new NpgsqlCommand(
            "INSERT INTO agent_event(room_id, event_type) VALUES (@room, 'unmatched');",
            connection);
        log.Parameters.AddWithValue("room", roomId);
        await log.ExecuteNonQueryAsync(cancellationToken);
    }

}

