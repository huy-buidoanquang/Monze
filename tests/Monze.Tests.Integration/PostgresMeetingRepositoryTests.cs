using Monze.Infrastructure.Persistence;
using Monze.Application.Commands;
using Npgsql;
using Monze.Testing;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresMeetingRepositoryTests
{
    [DbFact]
    public async Task Direct_agent_binding_creates_voice_chat_session_and_posts_summary_to_that_channel()
    {
        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        const long clanId = 2104288434238525440L;
        // Keep the integration fixture isolated from live dev meetings that may
        // legitimately leave a suggested session for the real voice channel.
        const long voiceChannelId = 987654321012345678L;
        var roomId = $"monze-agent-direct-test:{Guid.NewGuid():N}";
        var dedupeKey = string.Empty;

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        long sessionId = 0;
        try
        {
            var binding = await repository.BindAgentSessionAsync(
                clanId,
                voiceChannelId,
                roomId,
                2061341035941859328L,
                CancellationToken.None);

            Assert.NotNull(binding);
            sessionId = binding!.SessionId;
            Assert.Equal(voiceChannelId, binding.TextChannelId);
            Assert.Equal(voiceChannelId, binding.VoiceChannelId);
            Assert.True(binding.DirectAgent);

            var ended = await repository.MarkMeetingEndedAsync(roomId, CancellationToken.None);
            Assert.NotNull(ended);
            Assert.Equal(sessionId, ended!.SessionId);

            Assert.True(await repository.StoreSummaryAsync(
                roomId,
                "direct Agent summary",
                null,
                CancellationToken.None));

            dedupeKey = $"meeting:{sessionId}";
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var verify = new NpgsqlCommand("""
                SELECT s.status, s.text_channel_id, s.voice_channel_id,
                       o.channel_id, o.reply_to_message_id
                FROM meeting_session s
                JOIN outbox_delivery o ON o.meeting_session_id = s.id
                WHERE s.id = @session AND o.dedupe_key = @dedupe;
                """, connection);
            verify.Parameters.AddWithValue("session", sessionId);
            verify.Parameters.AddWithValue("dedupe", dedupeKey);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("posted", reader.GetString(0));
            Assert.Equal(voiceChannelId, reader.GetInt64(1));
            Assert.Equal(voiceChannelId, reader.GetInt64(2));
            Assert.Equal(voiceChannelId, reader.GetInt64(3));
            Assert.True(reader.IsDBNull(4));
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM outbox_delivery WHERE dedupe_key = @dedupe;
                DELETE FROM meeting_summary WHERE session_id = @session;
                DELETE FROM voice_claim WHERE session_id = @session;
                DELETE FROM meeting_session WHERE id = @session;
                """, connection);
            cleanup.Parameters.AddWithValue("dedupe", dedupeKey);
            cleanup.Parameters.AddWithValue("session", sessionId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [DbFact]
    public async Task Store_summary_uses_text_channel_and_drops_cross_channel_reply()
    {
        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        const long clanId = 2104288434238525440L;
        const long textChannelId = 2104288438869037056L;
        const long voiceChannelId = 2104292344168714240L;
        const long notificationChannelId = 2104293328093712384L;
        const long notificationMessageId = 1840651258350000001L;
        var roomId = $"monze-summary-test:{Guid.NewGuid():N}";

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        long sessionId = 0;
        var dedupeKey = string.Empty;
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("""
                INSERT INTO meeting_session(
                    clan_id, text_channel_id, voice_channel_id, requester_id,
                    status, room_id, notification_channel_id, notification_message_id,
                    started_at, ended_at)
                VALUES (
                    @clan, @text, @voice, @requester,
                    'summary_pending', @room, @notify_channel, @notify_message,
                    now() - interval '5 minutes', now())
                RETURNING id;
                """, connection))
            {
                command.Parameters.AddWithValue("clan", clanId);
                command.Parameters.AddWithValue("text", textChannelId);
                command.Parameters.AddWithValue("voice", voiceChannelId);
                command.Parameters.AddWithValue("requester", -Random.Shared.NextInt64(1, long.MaxValue));
                command.Parameters.AddWithValue("room", roomId);
                command.Parameters.AddWithValue("notify_channel", notificationChannelId);
                command.Parameters.AddWithValue("notify_message", notificationMessageId);
                sessionId = (long)(await command.ExecuteScalarAsync() ?? 0L);
            }

            dedupeKey = $"meeting:{sessionId}";
            Assert.True(await repository.StoreSummaryAsync(
                roomId,
                "summary test",
                null,
                CancellationToken.None));

            await using var verifyConnection = await dataSource.OpenConnectionAsync();
            await using var verify = new NpgsqlCommand("""
                SELECT o.channel_id, o.reply_to_message_id, o.meeting_session_id,
                       s.status, ms.summary_text
                FROM outbox_delivery o
                JOIN meeting_session s ON s.id = o.meeting_session_id
                JOIN meeting_summary ms ON ms.session_id = s.id
                WHERE o.dedupe_key = @dedupe;
                """, verifyConnection);
            verify.Parameters.AddWithValue("dedupe", dedupeKey);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(textChannelId, reader.GetInt64(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(sessionId, reader.GetInt64(2));
            Assert.Equal("posted", reader.GetString(3));
            Assert.Equal("summary test", reader.GetString(4));
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM outbox_delivery WHERE dedupe_key = @dedupe;
                DELETE FROM meeting_summary WHERE session_id = @session;
                DELETE FROM meeting_session WHERE id = @session;
                """, connection);
            cleanup.Parameters.AddWithValue("dedupe", dedupeKey);
            cleanup.Parameters.AddWithValue("session", sessionId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [DbFact]
    public async Task Store_summary_falls_back_to_the_meeting_text_channel()
    {
        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        const long clanId = 2104288434238525440L;
        const long textChannelId = 2104288438869037056L;
        const long voiceChannelId = 2104292344168714240L;
        var roomId = $"monze-summary-text-fallback:{Guid.NewGuid():N}";
        var dedupeKey = string.Empty;

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        long sessionId = 0;
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("""
                INSERT INTO meeting_session(
                    clan_id, text_channel_id, voice_channel_id, requester_id,
                    status, room_id, started_at, ended_at)
                VALUES (
                    @clan, @text, @voice, @requester,
                    'summary_pending', @room,
                    now() - interval '5 minutes', now())
                RETURNING id;
                """, connection))
            {
                command.Parameters.AddWithValue("clan", clanId);
                command.Parameters.AddWithValue("text", textChannelId);
                command.Parameters.AddWithValue("voice", voiceChannelId);
                command.Parameters.AddWithValue("requester", -Random.Shared.NextInt64(1, long.MaxValue));
                command.Parameters.AddWithValue("room", roomId);
                sessionId = (long)(await command.ExecuteScalarAsync() ?? 0L);
            }

            dedupeKey = $"meeting:{sessionId}";
            Assert.True(await repository.StoreSummaryAsync(
                roomId,
                "text channel summary",
                null,
                CancellationToken.None));

            await using var verifyConnection = await dataSource.OpenConnectionAsync();
            await using var verify = new NpgsqlCommand("""
                SELECT channel_id, reply_to_message_id
                FROM outbox_delivery
                WHERE dedupe_key = @dedupe;
                """, verifyConnection);
            verify.Parameters.AddWithValue("dedupe", dedupeKey);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(textChannelId, reader.GetInt64(0));
            Assert.True(reader.IsDBNull(1));
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM outbox_delivery WHERE dedupe_key = @dedupe;
                DELETE FROM meeting_summary WHERE session_id = @session;
                DELETE FROM meeting_session WHERE id = @session;
                """, connection);
            cleanup.Parameters.AddWithValue("dedupe", dedupeKey);
            cleanup.Parameters.AddWithValue("session", sessionId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [DbFact]
    public async Task Summary_retry_exhaustion_is_terminal_and_enqueues_one_failure_notice()
    {
        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        const long clanId = 2104288434238525440L;
        const long textChannelId = 2104288438869037056L;
        const long voiceChannelId = 2104292344168714240L;
        var roomId = $"monze-summary-exhaustion:{Guid.NewGuid():N}";
        var dedupeKey = string.Empty;
        long sessionId = 0;

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("""
                INSERT INTO meeting_session(
                    clan_id, text_channel_id, voice_channel_id, requester_id,
                    status, room_id, summary_attempts)
                VALUES (
                    @clan, @text, @voice, @requester,
                    'summary_pending', @room, 7)
                RETURNING id;
                """, connection))
            {
                command.Parameters.AddWithValue("clan", clanId);
                command.Parameters.AddWithValue("text", textChannelId);
                command.Parameters.AddWithValue("voice", voiceChannelId);
                command.Parameters.AddWithValue("requester", -Random.Shared.NextInt64(1, long.MaxValue));
                command.Parameters.AddWithValue("room", roomId);
                sessionId = (long)(await command.ExecuteScalarAsync() ?? 0L);
            }

            dedupeKey = $"meeting-summary-failed:{sessionId}";
            await repository.RecordSummaryRetryAsync(roomId, "summary-empty", CancellationToken.None);
            await repository.RecordSummaryRetryAsync(roomId, "summary-empty-replay", CancellationToken.None);

            await using var verifyConnection = await dataSource.OpenConnectionAsync();
            await using var verify = new NpgsqlCommand("""
                SELECT s.status, s.summary_attempts, s.summary_next_attempt_at,
                       o.channel_id, o.kind, o.body, count(*) OVER ()
                FROM meeting_session s
                LEFT JOIN outbox_delivery o ON o.dedupe_key = @dedupe
                WHERE s.id = @session;
                """, verifyConnection);
            verify.Parameters.AddWithValue("dedupe", dedupeKey);
            verify.Parameters.AddWithValue("session", sessionId);
            await using (var reader = await verify.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("summary_failed", reader.GetString(0));
                Assert.Equal(8, reader.GetInt32(1));
                Assert.True(reader.IsDBNull(2));
                Assert.Equal(textChannelId, reader.GetInt64(3));
                Assert.Equal("MeetingSummary", reader.GetString(4));
                Assert.Equal(MonzeMessages.SummaryFailed, reader.GetString(5));
                Assert.Equal(1, reader.GetInt64(6));
            }

            await using (var reset = new NpgsqlCommand("""
                DELETE FROM outbox_delivery WHERE dedupe_key = @dedupe;
                UPDATE meeting_session
                SET status = 'summary_pending', summary_attempts = 8
                WHERE id = @session;
                """, verifyConnection))
            {
                reset.Parameters.AddWithValue("dedupe", dedupeKey);
                reset.Parameters.AddWithValue("session", sessionId);
                await reset.ExecuteNonQueryAsync();
            }

            var pending = await repository.ListPendingSummariesAsync(32, CancellationToken.None);
            Assert.DoesNotContain(pending, item => item.RoomId == roomId);

            await using (var repaired = new NpgsqlCommand("""
                SELECT status, summary_next_attempt_at,
                       (SELECT count(*) FROM outbox_delivery WHERE dedupe_key = @dedupe)
                FROM meeting_session
                WHERE id = @session;
                """, verifyConnection))
            {
                repaired.Parameters.AddWithValue("dedupe", dedupeKey);
                repaired.Parameters.AddWithValue("session", sessionId);
                await using var repairedReader = await repaired.ExecuteReaderAsync();
                Assert.True(await repairedReader.ReadAsync());
                Assert.Equal("summary_failed", repairedReader.GetString(0));
                Assert.True(repairedReader.IsDBNull(1));
                Assert.Equal(1, repairedReader.GetInt64(2));
            }
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM outbox_delivery WHERE dedupe_key = @dedupe;
                DELETE FROM meeting_session WHERE id = @session;
                """, connection);
            cleanup.Parameters.AddWithValue("dedupe", dedupeKey);
            cleanup.Parameters.AddWithValue("session", sessionId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [DbFact]
    public async Task Pending_summary_claims_are_distinct_across_four_concurrent_lanes()
    {
        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        const long clanId = -910000000000000004L;
        const long textChannelId = -910000000000000005L;
        const long voiceChannelId = -910000000000000006L;
        var roomPrefix = $"monze-summary-concurrent-claim:{Guid.NewGuid():N}:";

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("""
                INSERT INTO meeting_session(
                    clan_id, text_channel_id, voice_channel_id, requester_id,
                    status, room_id, summary_next_attempt_at)
                SELECT @clan, @text, @voice, @requester,
                       'summary_pending', @prefix || value::text,
                       '1900-01-01 00:00:00+00'::timestamptz
                FROM generate_series(1, 4) AS value;
                """, connection))
            {
                command.Parameters.AddWithValue("clan", clanId);
                command.Parameters.AddWithValue("text", textChannelId);
                command.Parameters.AddWithValue("voice", voiceChannelId);
                command.Parameters.AddWithValue("requester", clanId);
                command.Parameters.AddWithValue("prefix", roomPrefix);
                Assert.Equal(4, await command.ExecuteNonQueryAsync());
            }

            var claims = await Task.WhenAll(
                repository.ListPendingSummariesAsync(1, CancellationToken.None),
                repository.ListPendingSummariesAsync(1, CancellationToken.None),
                repository.ListPendingSummariesAsync(1, CancellationToken.None),
                repository.ListPendingSummariesAsync(1, CancellationToken.None));

            var roomIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var claim in claims)
            {
                var item = Assert.Single(claim);
                Assert.StartsWith(roomPrefix, item.RoomId, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(item.LeaseToken));
                Assert.True(roomIds.Add(item.RoomId));
            }

            Assert.Equal(4, roomIds.Count);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM meeting_session
                WHERE room_id LIKE @prefix || '%';
                """, connection);
            cleanup.Parameters.AddWithValue("prefix", roomPrefix);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
