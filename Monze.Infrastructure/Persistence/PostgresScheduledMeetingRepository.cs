using Monze.Application;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;

public sealed class PostgresScheduledMeetingRepository : IScheduledMeetingRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresScheduledMeetingRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<bool> CommitScheduledMeetingAsync(
        DueMeetingSchedule schedule,
        long voiceChannelId,
        DateTimeOffset claimUntil,
        DateTimeOffset? nextRunAt,
        string announcementBody,
        string contentJson,
        bool mentionEveryone,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var completeSchedule = new NpgsqlCommand("""
            UPDATE meeting_schedule
            SET status = CASE WHEN @next IS NULL THEN 'completed' ELSE 'active' END,
                next_run_at = COALESCE(@next, next_run_at),
                locked_until = NULL,
                lease_token = NULL,
                last_error = NULL
            WHERE id = @id AND status = 'running' AND lease_token = @lease;
            """, connection, transaction);
        completeSchedule.Parameters.AddWithValue("id", schedule.Id);
        completeSchedule.Parameters.AddWithValue("lease", schedule.LeaseToken);
        completeSchedule.Parameters.Add(new NpgsqlParameter("next", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)nextRunAt ?? DBNull.Value
        });
        if (await completeSchedule.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using var session = new NpgsqlCommand("""
            INSERT INTO meeting_session(
                clan_id, text_channel_id, requester_id, voice_channel_id, status, claim_until)
            VALUES (@clan, @channel, @requester, @voice, 'suggested', @claim_until)
            RETURNING id;
            """, connection, transaction);
        session.Parameters.AddWithValue("clan", schedule.ClanId);
        session.Parameters.AddWithValue("channel", schedule.ChannelId);
        session.Parameters.AddWithValue("requester", schedule.RequesterId);
        session.Parameters.AddWithValue("voice", voiceChannelId);
        session.Parameters.AddWithValue("claim_until", claimUntil);
        var sessionId = (long)(await session.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Scheduled meeting session was not created."));

        await using (var expirePrevious = new NpgsqlCommand("""
            UPDATE meeting_session AS previous
            SET status = 'expired', claim_until = NULL
            WHERE previous.id = (
                SELECT claim.session_id
                FROM voice_claim claim
                WHERE claim.clan_id = @clan
                  AND claim.voice_channel_id = @voice
                  AND claim.expires_at < now()
            )
              AND previous.status = 'suggested';
            """, connection, transaction))
        {
            expirePrevious.Parameters.AddWithValue("clan", schedule.ClanId);
            expirePrevious.Parameters.AddWithValue("voice", voiceChannelId);
            await expirePrevious.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var claim = new NpgsqlCommand("""
            INSERT INTO voice_claim(clan_id, voice_channel_id, session_id, expires_at)
            VALUES (@clan, @voice, @session, @claim_until)
            ON CONFLICT (clan_id, voice_channel_id)
            DO UPDATE SET session_id = EXCLUDED.session_id, expires_at = EXCLUDED.expires_at
            WHERE voice_claim.expires_at < now()
               OR voice_claim.session_id = EXCLUDED.session_id;
            """, connection, transaction))
        {
            claim.Parameters.AddWithValue("clan", schedule.ClanId);
            claim.Parameters.AddWithValue("voice", voiceChannelId);
            claim.Parameters.AddWithValue("session", sessionId);
            claim.Parameters.AddWithValue("claim_until", claimUntil);
            if (await claim.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO outbox_delivery(
                clan_id, channel_id, kind, dedupe_key, body, mention_everyone, content_json, meeting_session_id)
            VALUES (@clan, @channel, 'Announcement', @dedupe, @body, @mention, @content, @session)
            ON CONFLICT (dedupe_key) DO NOTHING;
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("clan", schedule.ClanId);
            outbox.Parameters.AddWithValue("channel", schedule.ChannelId);
            outbox.Parameters.AddWithValue(
                "dedupe",
                $"meeting-schedule:{schedule.Id}:{schedule.NextRunAt.UtcTicks}");
            outbox.Parameters.AddWithValue("body", announcementBody);
            outbox.Parameters.AddWithValue("mention", mentionEveryone);
            outbox.Parameters.AddWithValue("content", contentJson);
            outbox.Parameters.AddWithValue("session", sessionId);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
