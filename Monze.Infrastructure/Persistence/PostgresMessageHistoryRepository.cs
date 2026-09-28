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

    public async Task NoteMessageAsync(long clanId, long channelId, long messageId, bool gap, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO channel_policy(clan_id, channel_id, persist_messages, last_message_id, has_gap)
            VALUES (@clan, @channel, TRUE, @message, @gap)
            ON CONFLICT (clan_id, channel_id) DO UPDATE
            SET last_message_id = EXCLUDED.last_message_id, has_gap = channel_policy.has_gap OR EXCLUDED.has_gap;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("message", messageId);
        command.Parameters.AddWithValue("gap", gap);
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

