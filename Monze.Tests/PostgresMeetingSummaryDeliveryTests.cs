using Monze.Application;
using Monze.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresMeetingSummaryDeliveryTests
{
    [Fact]
    public async Task Summary_waits_for_the_invitation_ack_and_then_uses_it_as_the_reply_target()
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

        var suffix = Random.Shared.NextInt64(1, 1_000_000);
        var clanId = -9_650_000_000_000_000L - suffix;
        var textChannelId = clanId - 1;
        var voiceChannelId = clanId - 2;
        var roomId = $"monze-summary-before-invitation:{Guid.NewGuid():N}";
        const long invitationMessageId = 1840651258350000001L;
        var delivery = new MeetingSummaryDelivery(
            "{\"embed\":[{\"title\":\"summary\"}]}",
            "{\"embed\":[{\"title\":\"actions\"}]}");
        long sessionId = 0;

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var meeting = new PostgresMeetingRepository(dataSource);
        var outbox = new PostgresOutboxRepository(dataSource);
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("""
                INSERT INTO meeting_session(
                    clan_id, text_channel_id, voice_channel_id, requester_id,
                    status, room_id, started_at, ended_at,
                    voice_channel_label, direct_agent)
                VALUES (
                    @clan, @text, @voice, @requester,
                    'summary_pending', @room, now() - interval '2 minutes', now(),
                    'voice-room', FALSE)
                RETURNING id;
                """, connection))
            {
                command.Parameters.AddWithValue("clan", clanId);
                command.Parameters.AddWithValue("text", textChannelId);
                command.Parameters.AddWithValue("voice", voiceChannelId);
                command.Parameters.AddWithValue("requester", clanId - 3);
                command.Parameters.AddWithValue("room", roomId);
                sessionId = (long)(await command.ExecuteScalarAsync() ?? 0L);
            }

            Assert.True(await meeting.StoreSummaryAsync(
                roomId,
                "summary",
                null,
                delivery,
                CancellationToken.None));

            var beforeAck = await outbox.ClaimDueOutboxAsync(CancellationToken.None, clanId);
            Assert.DoesNotContain(beforeAck, item => item.MeetingSessionId == sessionId);

            await meeting.SetSessionInvitationMessageAsync(
                sessionId,
                textChannelId,
                invitationMessageId,
                CancellationToken.None);

            var firstBatch = await outbox.ClaimDueOutboxAsync(CancellationToken.None, clanId);
            var first = Assert.Single(firstBatch, item => item.MeetingSessionId == sessionId);
            Assert.Equal(invitationMessageId, first.ReplyToMessageId);
            Assert.False(first.ReplyDirectAgent);
            Assert.Equal(voiceChannelId, first.ReplyVoiceChannelId);
            Assert.Equal("voice-room", first.ReplyVoiceChannelLabel);
            await outbox.CompleteOutboxAsync(
                first.Id,
                first.LeaseToken,
                invitationMessageId + 1,
                false,
                CancellationToken.None);

            var secondBatch = await outbox.ClaimDueOutboxAsync(CancellationToken.None, clanId);
            var second = Assert.Single(secondBatch, item => item.MeetingSessionId == sessionId);
            Assert.Null(second.ReplyToMessageId);
            Assert.False(second.ReplyDirectAgent);
            Assert.Equal(voiceChannelId, second.ReplyVoiceChannelId);
            Assert.Equal("voice-room", second.ReplyVoiceChannelLabel);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM outbox_delivery WHERE meeting_session_id = @session;
                DELETE FROM meeting_summary WHERE session_id = @session;
                DELETE FROM voice_claim WHERE session_id = @session;
                DELETE FROM meeting_session WHERE id = @session;
                """, connection);
            cleanup.Parameters.AddWithValue("session", sessionId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Agent_summary_persists_full_transcript_and_orders_two_outbox_messages()
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

        var suffix = Random.Shared.NextInt64(1, 1_000_000);
        var clanId = -9_700_000_000_000_000L - suffix;
        var textChannelId = clanId - 1;
        var voiceChannelId = clanId - 2;
        var roomId = $"monze-summary-delivery:{Guid.NewGuid():N}";
        var delivery = new MeetingSummaryDelivery(
            "{\"embed\":[{\"title\":\"summary\"}]}",
            "{\"embed\":[{\"title\":\"actions\"}]}");
        long sessionId = 0;

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        var outbox = new PostgresOutboxRepository(dataSource);
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("""
                INSERT INTO meeting_session(
                    clan_id, text_channel_id, voice_channel_id, requester_id,
                    status, room_id, started_at, ended_at,
                    meeting_title, voice_channel_label, direct_agent)
                VALUES (
                    @clan, @text, @voice, @requester,
                    'summary_pending', @room, now() - interval '10 minutes', now(),
                    'Sprint Review', 'voice-room', TRUE)
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

            const string transcript = """
                {"room_id":"room","summary_data":{"summary":"summary"},"full_text":"full transcript"}
                """;
            Assert.True(await repository.StoreSummaryAsync(
                roomId,
                "summary",
                transcript,
                delivery,
                CancellationToken.None));

            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("""
                SELECT s.status,
                       ms.full_transcript::text,
                       first_item.id,
                       second_item.id,
                       second_item.depends_on_id
                FROM meeting_session s
                JOIN meeting_summary ms ON ms.session_id = s.id
                JOIN outbox_delivery first_item
                  ON first_item.dedupe_key = 'meeting:' || s.id::text
                JOIN outbox_delivery second_item
                  ON second_item.dedupe_key = 'meeting:' || s.id::text || ':actions'
                WHERE s.id = @session;
                """, connection))
            {
                command.Parameters.AddWithValue("session", sessionId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("posted", reader.GetString(0));
                Assert.Contains("full transcript", reader.GetString(1), StringComparison.Ordinal);
                var firstId = reader.GetInt64(2);
                var secondId = reader.GetInt64(3);
                Assert.Equal(firstId, reader.GetInt64(4));
                Assert.NotEqual(firstId, secondId);
            }

            var firstBatch = await outbox.ClaimDueOutboxAsync(CancellationToken.None, clanId);
            var first = Assert.Single(firstBatch, item => item.MeetingSessionId == sessionId && item.ContentJson == delivery.SummaryContentJson);
            Assert.Null(firstBatch.Single(item => item.Id == first.Id).ReplyToMessageId);
            Assert.True(first.ReplyDirectAgent);
            Assert.Equal(voiceChannelId, first.ReplyVoiceChannelId);
            Assert.Equal("voice-room", first.ReplyVoiceChannelLabel);
            await outbox.CompleteOutboxAsync(first.Id, first.LeaseToken, 1001, false, CancellationToken.None);

            var secondBatch = await outbox.ClaimDueOutboxAsync(CancellationToken.None, clanId);
            var second = Assert.Single(secondBatch, item => item.MeetingSessionId == sessionId && item.ContentJson == delivery.ActionItemsContentJson);
            Assert.Null(second.ReplyToMessageId);
            await outbox.CompleteOutboxAsync(second.Id, second.LeaseToken, 1002, false, CancellationToken.None);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM outbox_delivery WHERE meeting_session_id = @session;
                DELETE FROM meeting_summary WHERE session_id = @session;
                DELETE FROM voice_claim WHERE session_id = @session;
                DELETE FROM meeting_session WHERE id = @session;
                """, connection);
            cleanup.Parameters.AddWithValue("session", sessionId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
