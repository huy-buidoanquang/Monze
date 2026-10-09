using Monze.Domain;
using Monze.Infrastructure.Persistence;
using Npgsql;
using Monze.Testing;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresOutboxInvitationBindingTests
{
    [DbFact]
    public async Task Completing_scheduled_invitation_atomically_unblocks_all_cycle_summaries()
    {
        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var suffix = Random.Shared.NextInt64(1, 1_000_000);
        var clanId = -9_660_000_000_000_000L - suffix;
        var textChannelId = clanId - 1;
        var voiceChannelId = clanId - 2;
        var requesterId = clanId - 3;
        var invitationKey = $"meeting-invitation-atomic:{Guid.NewGuid():N}";
        var summaryKey = $"meeting-summary-atomic:{Guid.NewGuid():N}";
        var actionKey = $"meeting-actions-atomic:{Guid.NewGuid():N}";
        const long invitationMessageId = 1840651258350000001L;
        long rootSessionId = 0;
        long cycleSessionId = 0;

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresOutboxRepository(dataSource);
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("""
                WITH root AS (
                  INSERT INTO meeting_session(
                      clan_id, text_channel_id, voice_channel_id, requester_id,
                      status, direct_agent)
                  VALUES (@clan, @text, @voice, @requester, 'suggested', FALSE)
                  RETURNING id
                ), cycle AS (
                  INSERT INTO meeting_session(
                      clan_id, text_channel_id, voice_channel_id, requester_id,
                      status, room_id, direct_agent, root_session_id)
                  SELECT @clan, @text, @voice, @requester,
                         'posted', @room, FALSE, id
                  FROM root
                  RETURNING id, root_session_id
                ), invitation AS (
                  INSERT INTO outbox_delivery(
                      clan_id, channel_id, kind, dedupe_key, body,
                      meeting_session_id)
                  SELECT @clan, @text, 'Announcement', @invitation_key,
                         'invitation', root_session_id
                  FROM cycle
                ), summary AS (
                  INSERT INTO outbox_delivery(
                      clan_id, channel_id, kind, dedupe_key, body,
                      meeting_session_id)
                  SELECT @clan, @text, 'MeetingSummary', @summary_key,
                         'summary', id
                  FROM cycle
                  RETURNING id
                ), actions AS (
                  INSERT INTO outbox_delivery(
                      clan_id, channel_id, kind, dedupe_key, body,
                      meeting_session_id, depends_on_id)
                  SELECT @clan, @text, 'MeetingSummary', @action_key,
                         'actions', cycle.id, summary.id
                  FROM cycle
                  CROSS JOIN summary
                )
                SELECT root_session_id, id FROM cycle;
                """, connection))
            {
                command.Parameters.AddWithValue("clan", clanId);
                command.Parameters.AddWithValue("text", textChannelId);
                command.Parameters.AddWithValue("voice", voiceChannelId);
                command.Parameters.AddWithValue("requester", requesterId);
                command.Parameters.AddWithValue("room", $"atomic-room:{Guid.NewGuid():N}");
                command.Parameters.AddWithValue("invitation_key", invitationKey);
                command.Parameters.AddWithValue("summary_key", summaryKey);
                command.Parameters.AddWithValue("action_key", actionKey);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                rootSessionId = reader.GetInt64(0);
                cycleSessionId = reader.GetInt64(1);
            }

            var firstBatch = await repository.ClaimDueOutboxAsync(CancellationToken.None, clanId);
            var invitation = Assert.Single(
                firstBatch,
                item => item.Kind == OutboxKind.Announcement.ToString());
            Assert.DoesNotContain(firstBatch, item => item.Kind == OutboxKind.MeetingSummary.ToString());

            await repository.CompleteOutboxAsync(
                invitation.Id,
                invitation.LeaseToken,
                invitationMessageId,
                failed: false,
                CancellationToken.None);

            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("""
                SELECT count(*) FILTER (
                           WHERE notification_channel_id = @text
                             AND notification_message_id = @message
                             AND source_message_id = @message),
                       (SELECT reply_to_message_id
                        FROM outbox_delivery
                         WHERE dedupe_key = @summary_key),
                        (SELECT reply_to_message_id
                         FROM outbox_delivery
                         WHERE dedupe_key = @action_key)
                FROM meeting_session
                WHERE id IN (@root, @cycle);
                """, connection))
            {
                command.Parameters.AddWithValue("text", textChannelId);
                command.Parameters.AddWithValue("message", invitationMessageId);
                command.Parameters.AddWithValue("summary_key", summaryKey);
                command.Parameters.AddWithValue("action_key", actionKey);
                command.Parameters.AddWithValue("root", rootSessionId);
                command.Parameters.AddWithValue("cycle", cycleSessionId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2, reader.GetInt64(0));
                Assert.Equal(invitationMessageId, reader.GetInt64(1));
                Assert.True(reader.IsDBNull(2));
            }

            var secondBatch = await repository.ClaimDueOutboxAsync(CancellationToken.None, clanId);
            var summary = Assert.Single(
                secondBatch,
                item => item.Kind == OutboxKind.MeetingSummary.ToString());
            Assert.Equal(cycleSessionId, summary.MeetingSessionId);
            Assert.Equal(invitationMessageId, summary.ReplyToMessageId);
            await repository.CompleteOutboxAsync(
                summary.Id,
                summary.LeaseToken,
                invitationMessageId + 1,
                failed: false,
                CancellationToken.None);

            var thirdBatch = await repository.ClaimDueOutboxAsync(CancellationToken.None, clanId);
            var actions = Assert.Single(
                thirdBatch,
                item => item.Kind == OutboxKind.MeetingSummary.ToString());
            Assert.Equal(cycleSessionId, actions.MeetingSessionId);
            Assert.Null(actions.ReplyToMessageId);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM outbox_delivery
                WHERE dedupe_key IN (@invitation_key, @summary_key, @action_key);
                DELETE FROM meeting_session WHERE id = @cycle;
                DELETE FROM meeting_session WHERE id = @root;
                """, connection);
            cleanup.Parameters.AddWithValue("invitation_key", invitationKey);
            cleanup.Parameters.AddWithValue("summary_key", summaryKey);
            cleanup.Parameters.AddWithValue("action_key", actionKey);
            cleanup.Parameters.AddWithValue("cycle", cycleSessionId);
            cleanup.Parameters.AddWithValue("root", rootSessionId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
