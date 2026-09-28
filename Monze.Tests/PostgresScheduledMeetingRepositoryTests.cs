using Monze.Application;
using Monze.Domain;
using Monze.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresScheduledMeetingRepositoryTests
{
    [Fact]
    public async Task Scheduled_meeting_commit_is_atomic_and_lease_scoped()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MONZE_RUN_DB_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        const long clanId = 2104288434238525440L;
        const long channelId = 2104288438869037056L;
        var firstRequesterId = -Random.Shared.NextInt64(1, long.MaxValue);
        var secondRequesterId = firstRequesterId == long.MinValue
            ? long.MinValue + 1
            : firstRequesterId - 1;
        var voiceChannelId = -Random.Shared.NextInt64(1, long.MaxValue);
        var firstLease = $"scheduled-test:{Guid.NewGuid():N}";
        var secondLease = $"scheduled-test:{Guid.NewGuid():N}";
        var nextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var claimUntil = DateTimeOffset.UtcNow.AddMinutes(20);
        var firstDedupeKey = string.Empty;

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresScheduledMeetingRepository(dataSource);
        long firstScheduleId = 0;
        long secondScheduleId = 0;
        try
        {
            firstScheduleId = await InsertRunningScheduleAsync(firstRequesterId, firstLease);
            firstDedupeKey = $"meeting-schedule:{firstScheduleId}:{nextRunAt.UtcTicks}";
            var firstSchedule = new DueMeetingSchedule(
                firstScheduleId,
                clanId,
                channelId,
                firstRequesterId,
                MeetingScheduleKind.Once,
                "once",
                "Asia/Ho_Chi_Minh",
                nextRunAt,
                firstLease);

            Assert.True(await repository.CommitScheduledMeetingAsync(
                firstSchedule,
                voiceChannelId,
                claimUntil,
                null,
                "scheduled meeting test",
                CancellationToken.None));

            Assert.False(await repository.CommitScheduledMeetingAsync(
                firstSchedule,
                voiceChannelId,
                claimUntil,
                null,
                "scheduled meeting duplicate",
                CancellationToken.None));

            var firstState = await ReadStateAsync(firstScheduleId, firstRequesterId, voiceChannelId);
            Assert.Equal("completed", firstState.ScheduleStatus);
            Assert.True(firstState.ScheduleLeaseIsNull);
            Assert.Equal(1, firstState.SessionCount);
            Assert.Equal(1, firstState.VoiceClaimCount);
            Assert.Equal(1, firstState.OutboxCount);

            secondScheduleId = await InsertRunningScheduleAsync(secondRequesterId, secondLease);
            var secondSchedule = firstSchedule with
            {
                Id = secondScheduleId,
                RequesterId = secondRequesterId,
                LeaseToken = secondLease
            };

            Assert.False(await repository.CommitScheduledMeetingAsync(
                secondSchedule,
                voiceChannelId,
                claimUntil,
                null,
                "scheduled meeting conflict",
                CancellationToken.None));

            var secondState = await ReadStateAsync(secondScheduleId, secondRequesterId, voiceChannelId);
            Assert.Equal("running", secondState.ScheduleStatus);
            Assert.False(secondState.ScheduleLeaseIsNull);
            Assert.Equal(0, secondState.SessionCount);
            Assert.Equal(1, secondState.VoiceClaimCount);
            Assert.Equal(0, secondState.OutboxCount);
        }
        finally
        {
            await ExecuteAsync("""
                DELETE FROM outbox_delivery
                WHERE dedupe_key = @dedupe;
                DELETE FROM voice_claim WHERE clan_id = @clan AND voice_channel_id = @voice;
                DELETE FROM meeting_session
                WHERE clan_id = @clan AND requester_id = ANY(@requesters);
                DELETE FROM meeting_schedule WHERE id = ANY(@schedules);
                """, command =>
            {
                command.Parameters.AddWithValue("clan", clanId);
                command.Parameters.AddWithValue("voice", voiceChannelId);
                command.Parameters.AddWithValue("requesters", new[] { firstRequesterId, secondRequesterId });
                command.Parameters.AddWithValue("schedules", new[] { firstScheduleId, secondScheduleId });
                command.Parameters.AddWithValue("dedupe", firstDedupeKey);
            });
        }

        async Task<long> InsertRunningScheduleAsync(long requesterId, string leaseToken)
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO meeting_schedule(
                    clan_id, channel_id, requester_id, kind, when_text, timezone,
                    next_run_at, status, locked_until, lease_token)
                VALUES (
                    @clan, @channel, @requester, 'Once', 'once', 'Asia/Ho_Chi_Minh',
                    @next, 'running', now() + interval '60 seconds', @lease)
                RETURNING id;
                """, connection);
            command.Parameters.AddWithValue("clan", clanId);
            command.Parameters.AddWithValue("channel", channelId);
            command.Parameters.AddWithValue("requester", requesterId);
            command.Parameters.AddWithValue("next", nextRunAt);
            command.Parameters.AddWithValue("lease", leaseToken);
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        async Task<(string ScheduleStatus, bool ScheduleLeaseIsNull, int SessionCount, int VoiceClaimCount, int OutboxCount)> ReadStateAsync(
            long scheduleId,
            long requesterId,
            long voiceId)
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT s.status,
                       s.lease_token IS NULL,
                       (SELECT count(*) FROM meeting_session m
                        WHERE m.clan_id = @clan AND m.requester_id = @requester
                          AND m.voice_channel_id = @voice),
                       (SELECT count(*) FROM voice_claim c
                        WHERE c.clan_id = @clan AND c.voice_channel_id = @voice),
                       (SELECT count(*) FROM outbox_delivery o
                        WHERE o.clan_id = @clan
                          AND o.dedupe_key = 'meeting-schedule:' || @schedule::text || ':' || @next_ticks::text)
                FROM meeting_schedule s
                WHERE s.id = @schedule;
                """, connection);
            command.Parameters.AddWithValue("clan", clanId);
            command.Parameters.AddWithValue("requester", requesterId);
            command.Parameters.AddWithValue("voice", voiceId);
            command.Parameters.AddWithValue("schedule", scheduleId);
            command.Parameters.AddWithValue("next_ticks", nextRunAt.UtcTicks);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (
                reader.GetString(0),
                reader.GetBoolean(1),
                checked((int)reader.GetInt64(2)),
                checked((int)reader.GetInt64(3)),
                checked((int)reader.GetInt64(4)));
        }

        async Task ExecuteAsync(string sql, Action<NpgsqlCommand> configure)
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            configure(command);
            await command.ExecuteNonQueryAsync();
        }
    }
}
