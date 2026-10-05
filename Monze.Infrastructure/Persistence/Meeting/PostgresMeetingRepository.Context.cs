using Npgsql;

namespace Monze.Infrastructure.Persistence;

public sealed partial class PostgresMeetingRepository
{
    public async Task SetSessionInvitationMessageAsync(
        long sessionId,
        long channelId,
        long messageId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH target AS (
              SELECT COALESCE(root_session_id, id) AS root_id
              FROM meeting_session
              WHERE id = @session
            )
            , bound AS (
            UPDATE meeting_session AS item
            SET notification_channel_id = @channel,
                notification_message_id = @message,
                source_message_id = @message
            FROM target
            WHERE item.id = target.root_id
               OR item.root_session_id = target.root_id
            RETURNING item.id
            )
            UPDATE outbox_delivery AS delivery
            SET reply_to_message_id = @message
            FROM bound
            WHERE delivery.meeting_session_id = bound.id
              AND delivery.kind = 'MeetingSummary'
              AND delivery.depends_on_id IS NULL
              AND delivery.status IN ('pending', 'sending')
              AND delivery.external_message_id IS NULL;
            """, connection);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("message", messageId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetSessionStatusMessageAsync(
        long sessionId,
        long channelId,
        long messageId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE meeting_session
            SET notification_channel_id = @channel,
                notification_message_id = @message,
                source_message_id = COALESCE(source_message_id, @message)
            WHERE id = @session;
            """, connection);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("message", messageId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CloseMeetingContextAsync(
        long clanId,
        long voiceChannelId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE meeting_session
            SET context_closed_at = COALESCE(context_closed_at, now())
            WHERE clan_id = @clan
              AND voice_channel_id = @voice
              AND root_session_id IS NULL
              AND context_closed_at IS NULL;
            DELETE FROM voice_claim
            WHERE clan_id = @clan AND voice_channel_id = @voice;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("voice", voiceChannelId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CloseStartedMeetingContextsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH closed AS (
              UPDATE meeting_session
              SET context_closed_at = COALESCE(context_closed_at, now())
              WHERE root_session_id IS NULL
                AND context_closed_at IS NULL
                AND room_id IS NOT NULL
              RETURNING clan_id, voice_channel_id
            )
            DELETE FROM voice_claim AS claim
            USING closed
            WHERE claim.clan_id = closed.clan_id
              AND claim.voice_channel_id = closed.voice_channel_id;
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ExpireSuggestedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE meeting_session
            SET status = 'expired', context_closed_at = COALESCE(context_closed_at, @now)
            WHERE status = 'suggested' AND claim_until IS NOT NULL AND claim_until < @now;
            UPDATE meeting_session
            SET status = 'cancelled', context_closed_at = COALESCE(context_closed_at, @now)
            WHERE status = 'requested' AND created_at < @requested_cutoff;
            DELETE FROM voice_claim
            WHERE expires_at < @now;
            """, connection);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("requested_cutoff", now.AddMinutes(-5));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
