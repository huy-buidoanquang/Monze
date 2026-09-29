using Monze.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresMeetingRepositoryTests
{
    [Fact]
    public async Task Store_summary_uses_text_channel_and_drops_cross_channel_reply()
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

    [Fact]
    public async Task Store_summary_falls_back_to_the_meeting_text_channel()
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
}
