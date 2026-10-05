using Monze.Application;
using Monze.Domain;
using Monze.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresMeetingCycleTests
{
    [Fact]
    public async Task Duplicate_started_event_for_same_room_reuses_the_existing_session()
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
        var clanId = -9_400_000_000_000_000L - suffix;
        var voiceChannelId = clanId - 1;
        var roomId = $"monze-duplicate-start:{Guid.NewGuid():N}";
        long sessionId = 0;

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        try
        {
            var first = await repository.BindAgentSessionAsync(
                clanId,
                voiceChannelId,
                roomId,
                clanId - 2,
                CancellationToken.None,
                "voice-duplicate");
            Assert.NotNull(first);
            sessionId = first!.SessionId;

            var duplicate = await repository.BindAgentSessionAsync(
                clanId,
                voiceChannelId,
                roomId,
                clanId - 2,
                CancellationToken.None,
                "voice-duplicate");

            Assert.NotNull(duplicate);
            Assert.Equal(sessionId, duplicate!.SessionId);
            Assert.Equal(MeetingStatus.Live, duplicate.Status);

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM meeting_session WHERE room_id = @room;",
                connection);
            count.Parameters.AddWithValue("room", roomId);
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        }
        finally
        {
            await CleanupRoomAsync(dataSource, roomId);
        }
    }

    [Fact]
    public async Task Ended_before_started_is_applied_when_the_session_is_bound()
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
        var clanId = -9_500_000_000_000_000L - suffix;
        var voiceChannelId = clanId - 1;
        var roomId = $"monze-ended-before-start:{Guid.NewGuid():N}";

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        try
        {
            Assert.Null(await repository.MarkMeetingEndedAsync(roomId, CancellationToken.None));

            var binding = await repository.BindAgentSessionAsync(
                clanId,
                voiceChannelId,
                roomId,
                clanId - 2,
                CancellationToken.None,
                "voice-reordered");
            Assert.NotNull(binding);
            Assert.Equal(MeetingStatus.SummaryPending, binding!.Status);

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var verify = new NpgsqlCommand("""
                SELECT status, ended_at IS NOT NULL,
                       NOT EXISTS (
                           SELECT 1 FROM voice_claim
                           WHERE session_id = meeting_session.id
                       ),
                       NOT EXISTS (
                           SELECT 1 FROM agent_event
                           WHERE room_id = @room AND event_type = 'ended_pending'
                       )
                FROM meeting_session
                WHERE room_id = @room;
                """, connection);
            verify.Parameters.AddWithValue("room", roomId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("summary_pending", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
        }
        finally
        {
            await CleanupRoomAsync(dataSource, roomId);
        }
    }

    [Fact]
    public async Task Duplicate_ended_events_store_one_pending_transition()
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
        var roomId = $"monze-duplicate-ended:{Guid.NewGuid():N}";

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        try
        {
            var results = await Task.WhenAll(
                repository.MarkMeetingEndedAsync(roomId, CancellationToken.None),
                repository.MarkMeetingEndedAsync(roomId, CancellationToken.None),
                repository.MarkMeetingEndedAsync(roomId, CancellationToken.None),
                repository.MarkMeetingEndedAsync(roomId, CancellationToken.None));
            Assert.All(results, Assert.Null);

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var count = new NpgsqlCommand("""
                SELECT count(*)
                FROM agent_event
                WHERE room_id = @room AND event_type = 'ended_pending';
                """, connection);
            count.Parameters.AddWithValue("room", roomId);
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        }
        finally
        {
            await CleanupRoomAsync(dataSource, roomId);
        }
    }

    [Fact]
    public async Task Summary_ready_before_started_is_applied_when_the_session_is_bound()
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
        var clanId = -9_600_000_000_000_000L - suffix;
        var voiceChannelId = clanId - 1;
        var roomId = $"monze-summary-before-start:{Guid.NewGuid():N}";

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        try
        {
            await repository.MarkSummaryPendingAsync(roomId, CancellationToken.None);

            var binding = await repository.BindAgentSessionAsync(
                clanId,
                voiceChannelId,
                roomId,
                clanId - 2,
                CancellationToken.None,
                "voice-reordered");
            Assert.NotNull(binding);
            Assert.Equal(MeetingStatus.SummaryPending, binding!.Status);

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var verify = new NpgsqlCommand("""
                SELECT status, summary_next_attempt_at IS NOT NULL,
                       NOT EXISTS (
                           SELECT 1 FROM agent_event
                           WHERE room_id = @room AND event_type = 'summary_ready_pending'
                       )
                FROM meeting_session
                WHERE room_id = @room;
                """, connection);
            verify.Parameters.AddWithValue("room", roomId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("summary_pending", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }
        finally
        {
            await CleanupRoomAsync(dataSource, roomId);
        }
    }

    [Fact]
    public async Task Concurrent_started_and_ended_events_converge_to_summary_pending()
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
        var voiceChannelId = clanId - 1;
        var roomId = $"monze-concurrent-start-end:{Guid.NewGuid():N}";

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        try
        {
            var started = repository.BindAgentSessionAsync(
                clanId,
                voiceChannelId,
                roomId,
                clanId - 2,
                CancellationToken.None,
                "voice-concurrent");
            var ended = repository.MarkMeetingEndedAsync(roomId, CancellationToken.None);
            await Task.WhenAll(started, ended);

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var verify = new NpgsqlCommand("""
                SELECT count(*), min(status), bool_and(ended_at IS NOT NULL),
                       NOT EXISTS (
                           SELECT 1
                           FROM voice_claim
                           JOIN meeting_session AS claimed
                             ON claimed.id = voice_claim.session_id
                           WHERE claimed.room_id = @room
                       )
                FROM meeting_session AS min_session
                WHERE room_id = @room;
                """, connection);
            verify.Parameters.AddWithValue("room", roomId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("summary_pending", reader.GetString(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
        }
        finally
        {
            await CleanupRoomAsync(dataSource, roomId);
        }
    }

    [Fact]
    public async Task Late_ended_event_records_time_without_reverting_a_posted_summary()
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
        var clanId = -9_200_000_000_000_000L - suffix;
        var voiceChannelId = clanId - 1;
        var roomId = $"monze-summary-before-ended:{Guid.NewGuid():N}";
        long sessionId = 0;

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        try
        {
            var binding = await repository.BindAgentSessionAsync(
                clanId,
                voiceChannelId,
                roomId,
                clanId - 2,
                CancellationToken.None,
                "voice-race");
            Assert.NotNull(binding);
            sessionId = binding!.SessionId;
            Assert.True(await repository.StoreSummaryAsync(
                roomId,
                "summary arrived first",
                null,
                CancellationToken.None));

            Assert.Null(await repository.MarkMeetingEndedAsync(roomId, CancellationToken.None));

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var verify = new NpgsqlCommand(
                "SELECT status, ended_at IS NOT NULL FROM meeting_session WHERE id = @session;",
                connection);
            verify.Parameters.AddWithValue("session", sessionId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("posted", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
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
    public async Task Repeated_agent_cycles_keep_one_invitation_and_create_distinct_summaries()
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
        var clanId = -9_100_000_000_000_000L - suffix;
        var textChannelId = clanId - 1;
        var voiceChannelId = clanId - 2;
        var requesterId = clanId - 3;
        const long invitationMessageId = 1840651258350000001L;
        var firstRoom = $"monze-cycle-1:{Guid.NewGuid():N}";
        var secondRoom = $"monze-cycle-2:{Guid.NewGuid():N}";
        var thirdRoom = $"monze-cycle-3:{Guid.NewGuid():N}";
        var inboxKey = $"monze-agent-cycle:{Guid.NewGuid():N}";
        var sessionIds = new List<long>();

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        try
        {
            var rootSessionId = await repository.CreateMeetingAsync(
                clanId,
                textChannelId,
                requesterId,
                null,
                CancellationToken.None);
            sessionIds.Add(rootSessionId);
            Assert.True(await repository.SuggestMeetingAsync(
                rootSessionId,
                voiceChannelId,
                DateTimeOffset.UtcNow.AddMinutes(20),
                CancellationToken.None,
                "voice-cycle",
                "Cycle test"));
            await repository.SetSessionInvitationMessageAsync(
                rootSessionId,
                textChannelId,
                invitationMessageId,
                CancellationToken.None);

            var first = await repository.BindAgentSessionAsync(
                clanId,
                voiceChannelId,
                firstRoom,
                requesterId,
                CancellationToken.None,
                "voice-cycle");
            Assert.NotNull(first);
            Assert.Equal(rootSessionId, first!.SessionId);
            Assert.False(first.DirectAgent);
            Assert.Equal(invitationMessageId, first.SourceMessageId);

            Assert.NotNull(await repository.MarkMeetingEndedAsync(firstRoom, CancellationToken.None));
            Assert.True(await repository.StoreSummaryAsync(
                firstRoom,
                "first summary",
                null,
                Delivery(invitationMessageId, "first"),
                CancellationToken.None));

            var second = await repository.BindAgentSessionAsync(
                clanId,
                voiceChannelId,
                secondRoom,
                requesterId,
                CancellationToken.None,
                "voice-cycle");
            Assert.NotNull(second);
            sessionIds.Add(second!.SessionId);
            Assert.NotEqual(rootSessionId, second.SessionId);
            Assert.Equal(rootSessionId, second.RootSessionId);
            Assert.Equal(textChannelId, second.TextChannelId);
            Assert.Equal(invitationMessageId, second.SourceMessageId);

            Assert.NotNull(await repository.MarkMeetingEndedAsync(secondRoom, CancellationToken.None));
            Assert.True(await repository.StoreSummaryAsync(
                secondRoom,
                "second summary",
                null,
                Delivery(invitationMessageId, "second"),
                CancellationToken.None));

            Assert.True(await repository.TryClaimInboxAsync("agent", inboxKey, CancellationToken.None));
            Assert.False(await repository.TryClaimInboxAsync("agent", inboxKey, CancellationToken.None));

            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var verify = new NpgsqlCommand("""
                SELECT count(DISTINCT summary.session_id),
                       count(outbox.id),
                       count(*) FILTER (WHERE outbox.reply_to_message_id = @message)
                FROM meeting_summary AS summary
                JOIN meeting_session AS session ON session.id = summary.session_id
                JOIN outbox_delivery AS outbox ON outbox.meeting_session_id = session.id
                WHERE session.id = ANY(@sessions);
                """, connection))
            {
                verify.Parameters.AddWithValue("message", invitationMessageId);
                verify.Parameters.AddWithValue("sessions", sessionIds.ToArray());
                await using var reader = await verify.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2, reader.GetInt64(0));
                Assert.Equal(4, reader.GetInt64(1));
                Assert.Equal(2, reader.GetInt64(2));
            }

            await repository.CloseMeetingContextAsync(clanId, voiceChannelId, CancellationToken.None);
            var direct = await repository.BindAgentSessionAsync(
                clanId,
                voiceChannelId,
                thirdRoom,
                requesterId,
                CancellationToken.None,
                "voice-cycle");
            Assert.NotNull(direct);
            sessionIds.Add(direct!.SessionId);
            Assert.True(direct.DirectAgent);
            Assert.Equal(voiceChannelId, direct.TextChannelId);
            Assert.Null(direct.RootSessionId);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM inbox_event WHERE source = 'agent' AND event_key = @inbox;
                DELETE FROM outbox_delivery WHERE meeting_session_id = ANY(@sessions);
                DELETE FROM meeting_summary WHERE session_id = ANY(@sessions);
                DELETE FROM voice_claim WHERE session_id = ANY(@sessions);
                DELETE FROM meeting_session WHERE root_session_id = ANY(@sessions);
                DELETE FROM meeting_session WHERE id = ANY(@sessions);
                """, connection);
            cleanup.Parameters.AddWithValue("inbox", inboxKey);
            cleanup.Parameters.AddWithValue("sessions", sessionIds.ToArray());
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static MeetingSummaryDelivery Delivery(long replyToMessageId, string label)
        => new(
            $"{{\"rpl\":{replyToMessageId},\"embed\":[{{\"title\":\"{label}\"}}]}}",
            $"{{\"embed\":[{{\"title\":\"{label} actions\"}}]}}");

    private static async Task CleanupRoomAsync(NpgsqlDataSource dataSource, string roomId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var cleanup = new NpgsqlCommand("""
            DELETE FROM outbox_delivery
            WHERE meeting_session_id IN (
                SELECT id FROM meeting_session WHERE room_id = @room
            );
            DELETE FROM meeting_summary
            WHERE session_id IN (
                SELECT id FROM meeting_session WHERE room_id = @room
            );
            DELETE FROM voice_claim
            WHERE session_id IN (
                SELECT id FROM meeting_session WHERE room_id = @room
            );
            DELETE FROM meeting_session WHERE room_id = @room;
            DELETE FROM agent_event WHERE room_id = @room;
            """, connection);
        cleanup.Parameters.AddWithValue("room", roomId);
        await cleanup.ExecuteNonQueryAsync();
    }
}
