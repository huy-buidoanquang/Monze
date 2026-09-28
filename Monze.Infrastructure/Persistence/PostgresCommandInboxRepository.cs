using Monze.Application;
using Npgsql;

namespace Monze.Infrastructure.Persistence;

public sealed class PostgresCommandInboxRepository : ICommandInboxRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCommandInboxRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<CommandInboxLease?> TryClaimAsync(
        long clanId,
        long channelId,
        long messageId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO command_inbox(
                clan_id, channel_id, message_id, status, lease_token, locked_until)
            VALUES (
                @clan, @channel, @message, 'processing',
                md5(random()::text || clock_timestamp()::text),
                now() + interval '60 seconds')
            ON CONFLICT (clan_id, channel_id, message_id)
            DO UPDATE SET
                status = 'processing',
                lease_token = md5(random()::text || clock_timestamp()::text),
                locked_until = now() + interval '60 seconds',
                received_at = now(),
                completed_at = NULL
            WHERE command_inbox.status <> 'completed'
              AND command_inbox.locked_until <= now()
            RETURNING clan_id, channel_id, message_id, lease_token;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("message", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CommandInboxLease(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3));
    }

    public async Task CompleteAsync(CommandInboxLease lease, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE command_inbox
            SET status = 'completed',
                completed_at = now(),
                locked_until = now()
            WHERE clan_id = @clan
              AND channel_id = @channel
              AND message_id = @message
              AND status = 'processing'
              AND lease_token = @lease;
            """, connection);
        AddLeaseParameters(command, lease);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseAsync(CommandInboxLease lease, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM command_inbox
            WHERE clan_id = @clan
              AND channel_id = @channel
              AND message_id = @message
              AND status = 'processing'
              AND lease_token = @lease;
            """, connection);
        AddLeaseParameters(command, lease);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM command_inbox
            WHERE completed_at < @before
               OR (status = 'processing' AND locked_until < @before);
            """, connection);
        command.Parameters.AddWithValue("before", before);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddLeaseParameters(NpgsqlCommand command, CommandInboxLease lease)
    {
        command.Parameters.AddWithValue("clan", lease.ClanId);
        command.Parameters.AddWithValue("channel", lease.ChannelId);
        command.Parameters.AddWithValue("message", lease.MessageId);
        command.Parameters.AddWithValue("lease", lease.Token);
    }
}
