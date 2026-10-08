using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;
public sealed partial class PostgresMeetingRepository : IMeetingRepository
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

    public async Task<IReadOnlySet<long>> ActiveVoiceClaimsAsync(
        long clanId,
        CancellationToken cancellationToken)
    {
        var claims = new HashSet<long>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT voice_channel_id
            FROM voice_claim
            WHERE clan_id = @clan AND expires_at > now();
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claims.Add(reader.GetInt64(0));
        }

        return claims;
    }


    public async Task<bool> SuggestMeetingAsync(
        long sessionId,
        long voiceChannelId,
        DateTimeOffset claimUntil,
        CancellationToken cancellationToken,
        string? voiceChannelLabel = null,
        string? meetingTitle = null)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var session = new NpgsqlCommand("""
            UPDATE meeting_session
            SET status = 'suggested', voice_channel_id = @voice, claim_until = @until,
                voice_channel_label = COALESCE(@label, voice_channel_label),
                meeting_title = COALESCE(@title, meeting_title)
            WHERE id = @id AND status = 'requested';
            """, connection, tx);
        session.Parameters.AddWithValue("id", sessionId);
        session.Parameters.AddWithValue("voice", voiceChannelId);
        session.Parameters.AddWithValue("until", claimUntil);
        session.Parameters.AddWithValue("label", (object?)voiceChannelLabel ?? DBNull.Value);
        session.Parameters.AddWithValue("title", (object?)meetingTitle ?? DBNull.Value);
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

        // Only a suggestion that won the room closes the room's previous context;
        // a losing one must leave the meeting holding the room untouched (CAND-15).
        await using (var closePreviousContext = new NpgsqlCommand("""
            UPDATE meeting_session
            SET context_closed_at = COALESCE(context_closed_at, now())
            WHERE clan_id = (SELECT clan_id FROM meeting_session WHERE id = @id)
              AND voice_channel_id = @voice
              AND root_session_id IS NULL
              AND context_closed_at IS NULL
              AND id <> @id;
            """, connection, tx))
        {
            closePreviousContext.Parameters.AddWithValue("id", sessionId);
            closePreviousContext.Parameters.AddWithValue("voice", voiceChannelId);
            await closePreviousContext.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<MeetingSessionBinding?> BindAgentSessionAsync(
        long clanId,
        long voiceChannelId,
        string roomId,
        long requesterId,
        CancellationToken cancellationToken,
        string? voiceChannelLabel = null)
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

        MeetingSessionBinding? binding = null;
        string? existingRoomId = null;
        var matchedExistingRoom = false;

        await using (var exactRoom = new NpgsqlCommand("""
            SELECT id, clan_id, text_channel_id, voice_channel_id, direct_agent,
                   notification_message_id, notification_channel_id,
                   meeting_title, voice_channel_label, source_message_id,
                   root_session_id, status
            FROM meeting_session
            WHERE room_id = @room
            FOR UPDATE;
            """, connection, transaction))
        {
            exactRoom.Parameters.AddWithValue("room", roomId);
            await using var reader = await exactRoom.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var storedClanId = reader.GetInt64(1);
                var storedVoiceChannelId = reader.GetInt64(3);
                if (storedClanId != clanId || storedVoiceChannelId != voiceChannelId)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }

                binding = new MeetingSessionBinding(
                    reader.GetInt64(0),
                    storedClanId,
                    reader.GetInt64(2),
                    storedVoiceChannelId,
                    reader.GetBoolean(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    ReadMeetingStatus(reader.GetString(11)));
                existingRoomId = roomId;
                matchedExistingRoom = true;
            }
        }

        if (binding is null)
        {
            await using var existing = new NpgsqlCommand("""
                SELECT id, text_channel_id, voice_channel_id, direct_agent,
                       notification_message_id, notification_channel_id,
                       meeting_title, voice_channel_label, source_message_id,
                       root_session_id, room_id, status
                FROM meeting_session
                WHERE clan_id = @clan
                  AND voice_channel_id = @voice
                  AND root_session_id IS NULL
                  AND context_closed_at IS NULL
                  AND status NOT IN ('cancelled', 'expired')
                ORDER BY id DESC
                LIMIT 1
                FOR UPDATE;
                """, connection, transaction);
            existing.Parameters.AddWithValue("clan", clanId);
            existing.Parameters.AddWithValue("voice", voiceChannelId);
            await using var existingReader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await existingReader.ReadAsync(cancellationToken))
            {
                var sessionId = existingReader.GetInt64(0);
                var textChannelId = existingReader.GetInt64(1);
                var voiceId = existingReader.GetInt64(2);
                var direct = existingReader.GetBoolean(3);
                long? messageId = existingReader.IsDBNull(4) ? null : existingReader.GetInt64(4);
                long? notificationChannelId = existingReader.IsDBNull(5) ? null : existingReader.GetInt64(5);
                var meetingTitle = existingReader.IsDBNull(6) ? null : existingReader.GetString(6);
                var storedVoiceChannelLabel = existingReader.IsDBNull(7) ? null : existingReader.GetString(7);
                long? sourceMessageId = existingReader.IsDBNull(8) ? null : existingReader.GetInt64(8);
                long? rootSessionId = existingReader.IsDBNull(9) ? null : existingReader.GetInt64(9);
                existingRoomId = existingReader.IsDBNull(10) ? null : existingReader.GetString(10);
                binding = new MeetingSessionBinding(
                    sessionId,
                    clanId,
                    textChannelId,
                    voiceId,
                    direct,
                    messageId,
                    notificationChannelId,
                    meetingTitle,
                    storedVoiceChannelLabel,
                    sourceMessageId,
                    rootSessionId,
                    ReadMeetingStatus(existingReader.GetString(11)));
            }
        }

        if (binding is null)
        {
            await using var create = new NpgsqlCommand("""
                INSERT INTO meeting_session(
                    clan_id, text_channel_id, voice_channel_id, requester_id,
                    status, room_id, direct_agent, notification_channel_id, started_at,
                    voice_channel_label)
                VALUES (@clan, @voice, @voice, @requester, 'live', @room, TRUE, @voice, now(), @label)
                RETURNING id;
                """, connection, transaction);
            create.Parameters.AddWithValue("clan", clanId);
            create.Parameters.AddWithValue("voice", voiceChannelId);
            create.Parameters.AddWithValue("requester", requesterId);
            create.Parameters.AddWithValue("room", roomId);
            create.Parameters.AddWithValue("label", (object?)voiceChannelLabel ?? DBNull.Value);
            var sessionId = (long)(await create.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Agent meeting session was not created."));
            binding = new MeetingSessionBinding(
                sessionId,
                clanId,
                voiceChannelId,
                voiceChannelId,
                true,
                null,
                voiceChannelId,
                null,
                voiceChannelLabel,
                null,
                null);
        }
        else if (!matchedExistingRoom && string.IsNullOrWhiteSpace(existingRoomId))
        {
            await using var update = new NpgsqlCommand("""
                UPDATE meeting_session
                SET status = 'live', room_id = @room,
                    started_at = COALESCE(started_at, now()),
                    claim_until = NULL,
                    voice_channel_label = COALESCE(voice_channel_label, @label)
                WHERE id = @id;
                """, connection, transaction);
            update.Parameters.AddWithValue("id", binding.SessionId);
            update.Parameters.AddWithValue("room", roomId);
            update.Parameters.AddWithValue("label", (object?)voiceChannelLabel ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(binding.VoiceChannelLabel)
                && !string.IsNullOrWhiteSpace(voiceChannelLabel))
            {
                binding = binding with { VoiceChannelLabel = voiceChannelLabel };
            }

            binding = binding with { Status = MeetingStatus.Live };
        }
        else if (!matchedExistingRoom)
        {
            var rootSessionId = binding.SessionId;
            await using var createCycle = new NpgsqlCommand("""
                INSERT INTO meeting_session(
                    clan_id, text_channel_id, voice_channel_id, requester_id,
                    status, room_id, direct_agent, notification_channel_id,
                    notification_message_id, source_message_id, started_at,
                    meeting_title, voice_channel_label, root_session_id)
                SELECT clan_id, text_channel_id, voice_channel_id, requester_id,
                       'live', @room, direct_agent, notification_channel_id,
                       notification_message_id, source_message_id, now(),
                       meeting_title, COALESCE(voice_channel_label, @label), id
                FROM meeting_session
                WHERE id = @root
                RETURNING id, clan_id, text_channel_id, voice_channel_id,
                          direct_agent, notification_message_id,
                          notification_channel_id, meeting_title,
                          voice_channel_label, source_message_id,
                          root_session_id;
                """, connection, transaction);
            createCycle.Parameters.AddWithValue("root", rootSessionId);
            createCycle.Parameters.AddWithValue("room", roomId);
            createCycle.Parameters.AddWithValue("label", (object?)voiceChannelLabel ?? DBNull.Value);
            await using var reader = await createCycle.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

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
                MeetingStatus.Live);
        }

        var hasPendingAgentCompletion = false;
        await using (var pending = new NpgsqlCommand("""
            DELETE FROM agent_event
            WHERE room_id = @room
              AND event_type IN ('ended_pending', 'summary_ready_pending')
            RETURNING event_type;
            """, connection, transaction))
        {
            pending.Parameters.AddWithValue("room", roomId);
            await using var reader = await pending.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hasPendingAgentCompletion = true;
            }
        }

        if (hasPendingAgentCompletion
            && binding.Status is MeetingStatus.Live or MeetingStatus.SummaryPending)
        {
            await using var markPending = new NpgsqlCommand("""
                UPDATE meeting_session
                SET status = 'summary_pending',
                    ended_at = COALESCE(ended_at, now()),
                    summary_next_attempt_at = COALESCE(summary_next_attempt_at, now()),
                    summary_last_error = NULL,
                    summary_locked_until = NULL,
                    summary_lease_token = NULL
                WHERE id = @session
                  AND status IN ('live', 'summary_pending');
                """, connection, transaction);
            markPending.Parameters.AddWithValue("session", binding.SessionId);
            await markPending.ExecuteNonQueryAsync(cancellationToken);
            binding = binding with { Status = MeetingStatus.SummaryPending };
        }

        if (binding.Status == MeetingStatus.Live)
        {
            await using var claim = new NpgsqlCommand("""
                INSERT INTO voice_claim(clan_id, voice_channel_id, session_id, expires_at)
                VALUES (@clan, @voice, @session, 'infinity'::timestamptz)
                ON CONFLICT (clan_id, voice_channel_id)
                DO UPDATE SET session_id = EXCLUDED.session_id,
                              expires_at = EXCLUDED.expires_at;
                """, connection, transaction);
            claim.Parameters.AddWithValue("clan", clanId);
            claim.Parameters.AddWithValue("voice", voiceChannelId);
            claim.Parameters.AddWithValue("session", binding.SessionId);
            await claim.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var releaseClaim = new NpgsqlCommand("""
                DELETE FROM voice_claim
                WHERE clan_id = @clan
                  AND voice_channel_id = @voice
                  AND session_id = @session;
                """, connection, transaction);
            releaseClaim.Parameters.AddWithValue("clan", clanId);
            releaseClaim.Parameters.AddWithValue("voice", voiceChannelId);
            releaseClaim.Parameters.AddWithValue("session", binding.SessionId);
            await releaseClaim.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return binding;
    }

    private static MeetingStatus ReadMeetingStatus(string status)
        => status switch
        {
            "requested" => MeetingStatus.Requested,
            "suggested" => MeetingStatus.Suggested,
            "live" => MeetingStatus.Live,
            "summary_pending" => MeetingStatus.SummaryPending,
            "summary_failed" => MeetingStatus.SummaryFailed,
            "posted" => MeetingStatus.Posted,
            "expired" => MeetingStatus.Expired,
            "cancelled" => MeetingStatus.Cancelled,
            _ => throw new InvalidOperationException($"Unsupported meeting status '{status}'.")
        };

}
