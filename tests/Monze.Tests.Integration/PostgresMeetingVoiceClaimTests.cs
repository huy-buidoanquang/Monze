using Monze.Infrastructure.Persistence;
using Npgsql;
using Monze.Testing;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresMeetingVoiceClaimTests
{
    [DbFact]
    public async Task Concurrent_voice_claims_keep_one_session_and_cancel_the_loser()
    {
        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var suffix = Random.Shared.NextInt64(1, 1_000_000);
        var clanId = -9_625_000_000_000_000L - suffix;
        var channelId = clanId - 1;
        var voiceChannelId = clanId - 2;
        long firstSessionId = 0;
        long secondSessionId = 0;

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresMeetingRepository(dataSource);
        try
        {
            firstSessionId = await repository.CreateMeetingAsync(
                clanId,
                channelId,
                clanId - 3,
                null,
                CancellationToken.None);
            secondSessionId = await repository.CreateMeetingAsync(
                clanId,
                channelId,
                clanId - 4,
                null,
                CancellationToken.None);

            var claimUntil = DateTimeOffset.UtcNow.AddMinutes(20);
            var results = await Task.WhenAll(
                repository.SuggestMeetingAsync(
                    firstSessionId,
                    voiceChannelId,
                    claimUntil,
                    CancellationToken.None,
                    "voice-room"),
                repository.SuggestMeetingAsync(
                    secondSessionId,
                    voiceChannelId,
                    claimUntil,
                    CancellationToken.None,
                    "voice-room"));

            Assert.Single(results, static succeeded => succeeded);
            Assert.Single(results, static succeeded => !succeeded);

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT session.id,
                       session.status,
                       session.voice_channel_id,
                       session.claim_until,
                       claim.session_id
                FROM meeting_session AS session
                LEFT JOIN voice_claim AS claim
                  ON claim.clan_id = session.clan_id
                 AND claim.voice_channel_id = @voice
                WHERE session.id IN (@first, @second)
                ORDER BY session.id;
                """, connection);
            command.Parameters.AddWithValue("voice", voiceChannelId);
            command.Parameters.AddWithValue("first", firstSessionId);
            command.Parameters.AddWithValue("second", secondSessionId);

            var suggestedSessionId = 0L;
            var cancelledRows = 0;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var sessionId = reader.GetInt64(0);
                var status = reader.GetString(1);
                if (status == "suggested")
                {
                    suggestedSessionId = sessionId;
                    Assert.Equal(voiceChannelId, reader.GetInt64(2));
                    Assert.False(reader.IsDBNull(3));
                    Assert.Equal(sessionId, reader.GetInt64(4));
                }
                else
                {
                    Assert.Equal("cancelled", status);
                    Assert.True(reader.IsDBNull(2));
                    Assert.True(reader.IsDBNull(3));
                    cancelledRows++;
                }
            }

            Assert.True(suggestedSessionId > 0);
            Assert.Equal(1, cancelledRows);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM voice_claim
                WHERE session_id IN (@first, @second);
                DELETE FROM meeting_session
                WHERE id IN (@first, @second);
                """, connection);
            cleanup.Parameters.AddWithValue("first", firstSessionId);
            cleanup.Parameters.AddWithValue("second", secondSessionId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
