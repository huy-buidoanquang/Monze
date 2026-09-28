using Monze.Application;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;
public sealed partial class PostgresOutboxRepository : IOutboxRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresOutboxRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task EnqueueAsync(long clanId, long channelId, OutboxKind kind, string dedupeKey, string body, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body)
            VALUES (@clan, @channel, @kind, @key, @body)
            ON CONFLICT (dedupe_key) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("kind", kind.ToString());
        command.Parameters.AddWithValue("key", dedupeKey);
        command.Parameters.AddWithValue("body", body);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListUncertainAsync(long clanId, CancellationToken cancellationToken)
    {
        var rows = new List<string>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id, kind, body FROM outbox_delivery WHERE clan_id = @clan AND status = 'uncertain' ORDER BY id;", connection);
        command.Parameters.AddWithValue("clan", clanId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add($"{reader.GetInt64(0)} {reader.GetString(1)} {reader.GetString(2)}");
        }

        return rows;
    }

    public async Task<bool> ResendAsync(long clanId, long outboxId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE outbox_delivery
            SET status = 'pending', attempts = 0, locked_until = NULL, lease_token = NULL,
                last_error = NULL, due_at = now()
            WHERE id = @id AND clan_id = @clan AND status = 'uncertain' AND external_message_id IS NULL;
            """, connection);
        command.Parameters.AddWithValue("id", outboxId);
        command.Parameters.AddWithValue("clan", clanId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

}

