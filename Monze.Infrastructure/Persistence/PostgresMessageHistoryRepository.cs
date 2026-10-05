using Monze.Application;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;
public sealed class PostgresMessageHistoryRepository : IMessageHistoryRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresMessageHistoryRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<bool> ChannelPersistsAsync(long clanId, long channelId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT persist_messages FROM channel_policy WHERE clan_id = @clan AND channel_id = @channel;", connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        return await command.ExecuteScalarAsync(cancellationToken) as bool? ?? false;
    }

    public async Task MarkChannelGapAsync(
        long clanId,
        long channelId,
        long messageId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE channel_policy
            SET last_message_id = GREATEST(
                    COALESCE(last_message_id, @message),
                    @message),
                has_gap = TRUE
            WHERE clan_id = @clan
              AND channel_id = @channel
              AND persist_messages;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("message", messageId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ChannelHasGapAsync(long clanId, long channelId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT has_gap FROM channel_policy WHERE clan_id = @clan AND channel_id = @channel;", connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        return await command.ExecuteScalarAsync(cancellationToken) as bool? ?? false;
    }

}

