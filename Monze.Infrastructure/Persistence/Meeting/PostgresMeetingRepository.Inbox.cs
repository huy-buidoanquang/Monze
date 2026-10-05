using Npgsql;

namespace Monze.Infrastructure.Persistence;

public sealed partial class PostgresMeetingRepository
{
    public async Task<bool> TryClaimInboxAsync(
        string source,
        string eventKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO inbox_event(source, event_key)
            VALUES (@source, @key)
            ON CONFLICT (source, event_key) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("key", eventKey);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task ReleaseInboxAsync(
        string source,
        string eventKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM inbox_event
            WHERE source = @source AND event_key = @key;
            """, connection);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("key", eventKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PurgeInboxAsync(
        DateTimeOffset before,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM inbox_event
            WHERE (source = 'agent' AND received_at < now() - interval '1 day')
               OR (source <> 'agent' AND received_at < @before);

            DELETE FROM agent_event
            WHERE created_at < now() - interval '1 day';
            """, connection);
        command.Parameters.AddWithValue("before", before);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
