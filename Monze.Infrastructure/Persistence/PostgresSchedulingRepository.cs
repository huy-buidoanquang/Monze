using Monze.Application;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;

public sealed class PostgresSchedulingRepository : ISchedulingRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresSchedulingRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<long> CreateMeetingScheduleAsync(
        long clanId,
        long channelId,
        long userId,
        string name,
        MeetingScheduleKind kind,
        string whenText,
        string timeZoneId,
        DateTimeOffset nextRunAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO meeting_schedule(
                clan_id, channel_id, requester_id, title, kind, when_text, timezone,
                next_run_at)
            VALUES (@clan, @channel, @requester, @title, @kind, @when, @timezone,
                    @next)
            RETURNING id;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("requester", userId);
        command.Parameters.AddWithValue("title", string.IsNullOrWhiteSpace(name) ? "Cuộc họp" : name);
        command.Parameters.AddWithValue("kind", kind.ToString());
        command.Parameters.AddWithValue("when", whenText);
        command.Parameters.AddWithValue("timezone", timeZoneId);
        command.Parameters.AddWithValue("next", nextRunAt);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task<IReadOnlyList<MeetingScheduleSummary>> ListMeetingSchedulesAsync(
        long clanId,
        long channelId,
        long userId,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = new List<MeetingScheduleSummary>(Math.Clamp(limit, 1, 50));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, title, kind, next_run_at, timezone, requester_id
            FROM meeting_schedule
            WHERE clan_id = @clan
              AND channel_id = @channel
              AND status IN ('active', 'running')
              AND (
                requester_id = @user
                OR EXISTS (
                  SELECT 1
                  FROM clan_registry c
                  LEFT JOIN clan_admin a
                    ON a.clan_id = c.clan_id AND a.user_id = @user
                  WHERE c.clan_id = @clan
                    AND c.inactive_reason IS NULL
                    AND (c.owner_id = @user OR a.user_id IS NOT NULL)
                )
              )
            ORDER BY next_run_at, id
            LIMIT @limit;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 50));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = Enum.TryParse<MeetingScheduleKind>(reader.GetString(2), true, out var parsed)
                ? parsed
                : MeetingScheduleKind.Once;
            rows.Add(new MeetingScheduleSummary(
                reader.GetInt64(0),
                reader.GetString(1),
                kind,
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetString(4),
                reader.GetInt64(5)));
        }

        return rows;
    }

    public async Task<bool> CancelMeetingScheduleAsync(
        long clanId,
        long channelId,
        long userId,
        long scheduleId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE meeting_schedule
            SET status = 'cancelled', locked_until = NULL, lease_token = NULL,
                last_error = NULL
            WHERE id = @id
              AND clan_id = @clan
              AND channel_id = @channel
              AND status IN ('active', 'running')
              AND (
                requester_id = @user
                OR EXISTS (
                  SELECT 1
                  FROM clan_registry c
                  LEFT JOIN clan_admin a
                    ON a.clan_id = c.clan_id AND a.user_id = @user
                  WHERE c.clan_id = @clan
                    AND c.inactive_reason IS NULL
                    AND (c.owner_id = @user OR a.user_id IS NOT NULL)
                )
              );
            """, connection);
        command.Parameters.AddWithValue("id", scheduleId);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("user", userId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<DueMeetingSchedule>> ClaimDueMeetingSchedulesAsync(
        CancellationToken cancellationToken)
    {
        var rows = new List<DueMeetingSchedule>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH due AS (
              SELECT id
              FROM meeting_schedule
              WHERE (status = 'active' OR (status = 'running' AND locked_until < now()))
                AND next_run_at <= now()
              ORDER BY next_run_at, id
              FOR UPDATE SKIP LOCKED
              LIMIT 128
            )
            UPDATE meeting_schedule AS item
            SET status = 'running',
                locked_until = now() + interval '60 seconds',
                lease_token = md5(random()::text || clock_timestamp()::text)
            FROM due
            WHERE item.id = due.id
            RETURNING item.id, item.clan_id, item.channel_id, item.requester_id,
                      item.kind, item.when_text, item.timezone, item.next_run_at,
                      item.lease_token, item.title;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = Enum.TryParse<MeetingScheduleKind>(
                reader.GetString(4),
                true,
                out var parsed)
                ? parsed
                : MeetingScheduleKind.Once;
            rows.Add(new DueMeetingSchedule(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                kind,
                reader.GetString(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetString(8),
                reader.GetString(9)));
        }

        return rows;
    }

    public async Task CompleteMeetingScheduleAsync(
        long id,
        string leaseToken,
        DateTimeOffset? nextRunAt,
        bool failed,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE meeting_schedule
            SET status = CASE
                    WHEN @failed THEN 'active'
                    WHEN @next IS NULL THEN 'completed'
                    ELSE 'active'
                END,
                next_run_at = COALESCE(@next, next_run_at),
                locked_until = NULL,
                lease_token = NULL
            WHERE id = @id AND lease_token = @lease;
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("lease", leaseToken);
        command.Parameters.AddWithValue("failed", failed);
        command.Parameters.Add(new NpgsqlParameter("next", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)nextRunAt ?? DBNull.Value
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

}
