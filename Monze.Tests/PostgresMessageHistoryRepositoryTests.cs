using Monze.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresMessageHistoryRepositoryTests
{
    [Fact]
    public async Task Message_watermark_is_monotonic_and_gap_state_is_sticky()
    {
        if (!PostgresTestEnabled())
        {
            return;
        }

        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var clanId = -Random.Shared.NextInt64(1, long.MaxValue);
        var channelId = -Random.Shared.NextInt64(1, long.MaxValue);
        var unconfiguredChannelId = channelId == long.MinValue ? long.MinValue + 1 : channelId - 1;
        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        await SeedClanAsync(dataSource, clanId, channelId);
        var repository = new PostgresMessageHistoryRepository(dataSource);
        try
        {
            await repository.MarkChannelGapAsync(clanId, channelId, 300, CancellationToken.None);
            await repository.MarkChannelGapAsync(clanId, channelId, 100, CancellationToken.None);

            Assert.True(await repository.ChannelPersistsAsync(clanId, channelId, CancellationToken.None));
            Assert.True(await repository.ChannelHasGapAsync(clanId, channelId, CancellationToken.None));

            await repository.MarkChannelGapAsync(
                clanId,
                unconfiguredChannelId,
                400,
                CancellationToken.None);
            Assert.False(await repository.ChannelPersistsAsync(
                clanId,
                unconfiguredChannelId,
                CancellationToken.None));

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT last_message_id, has_gap FROM channel_policy
                WHERE clan_id = @clan AND channel_id = @channel;
                """, connection);
            command.Parameters.AddWithValue("clan", clanId);
            command.Parameters.AddWithValue("channel", channelId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(300L, reader.GetInt64(0));
            Assert.True(reader.GetBoolean(1));
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM channel_policy WHERE clan_id = @clan;
                DELETE FROM clan_settings WHERE clan_id = @clan;
                DELETE FROM clan_registry WHERE clan_id = @clan;
                """, connection);
            cleanup.Parameters.AddWithValue("clan", clanId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static bool PostgresTestEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable("MONZE_RUN_DB_TESTS"),
            "1",
            StringComparison.Ordinal);

    private static async Task SeedClanAsync(NpgsqlDataSource dataSource, long clanId, long channelId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO clan_registry(clan_id, owner_id) VALUES (@clan, 1);
            INSERT INTO clan_settings(clan_id) VALUES (@clan);
            INSERT INTO channel_policy(clan_id, channel_id, persist_messages, last_message_id)
            VALUES (@clan, @channel, TRUE, 200);
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        await command.ExecuteNonQueryAsync();
    }
}
