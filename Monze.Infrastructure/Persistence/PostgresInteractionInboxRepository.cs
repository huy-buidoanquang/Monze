using Monze.Application;
using Npgsql;

namespace Monze.Infrastructure.Persistence;

public sealed class PostgresInteractionInboxRepository : IInteractionInboxRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresInteractionInboxRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<InteractionInboxLease?> TryClaimAsync(
        long clanId,
        long channelId,
        long messageId,
        string action,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO interaction_inbox(
                clan_id, channel_id, message_id, action,
                status, lease_token, locked_until)
            VALUES (
                @clan, @channel, @message, @action, 'processing',
                md5(random()::text || clock_timestamp()::text),
                now() + interval '60 seconds')
            ON CONFLICT (clan_id, channel_id, message_id, action)
            DO UPDATE SET
                status = 'processing',
                lease_token = md5(random()::text || clock_timestamp()::text),
                locked_until = now() + interval '60 seconds',
                received_at = now(),
                completed_at = NULL
            WHERE interaction_inbox.status = 'processing'
              AND interaction_inbox.locked_until <= now()
            RETURNING clan_id, channel_id, message_id, action, lease_token;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("message", messageId);
        command.Parameters.AddWithValue("action", action);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InteractionInboxLease(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4));
    }

    public Task<bool> CompleteAsync(
        InteractionInboxLease lease,
        CancellationToken cancellationToken)
        => FinishAsync(lease, "completed", cancellationToken);

    public Task<bool> MarkUncertainAsync(
        InteractionInboxLease lease,
        CancellationToken cancellationToken)
        => FinishAsync(lease, "uncertain", cancellationToken);

    public async Task ReleaseAsync(
        InteractionInboxLease lease,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM interaction_inbox
            WHERE clan_id = @clan
              AND channel_id = @channel
              AND message_id = @message
              AND action = @action
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
            DELETE FROM interaction_inbox
            WHERE completed_at < @before
               OR (status = 'processing' AND locked_until < @before);
            """, connection);
        command.Parameters.AddWithValue("before", before);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> FinishAsync(
        InteractionInboxLease lease,
        string status,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE interaction_inbox
            SET status = @status,
                completed_at = now(),
                locked_until = now()
            WHERE clan_id = @clan
              AND channel_id = @channel
              AND message_id = @message
              AND action = @action
              AND status = 'processing'
              AND lease_token = @lease;
            """, connection);
        command.Parameters.AddWithValue("status", status);
        AddLeaseParameters(command, lease);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static void AddLeaseParameters(
        NpgsqlCommand command,
        InteractionInboxLease lease)
    {
        command.Parameters.AddWithValue("clan", lease.ClanId);
        command.Parameters.AddWithValue("channel", lease.ChannelId);
        command.Parameters.AddWithValue("message", lease.MessageId);
        command.Parameters.AddWithValue("action", lease.Action);
        command.Parameters.AddWithValue("lease", lease.Token);
    }
}
