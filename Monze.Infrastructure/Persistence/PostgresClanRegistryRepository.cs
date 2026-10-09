using Monze.Application;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;
public sealed class PostgresClanRegistryRepository : IClanRegistryRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresClanRegistryRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<KnownClan>> ListClansAsync(CancellationToken cancellationToken)
    {
        var rows = new List<KnownClan>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT clan_id, owner_id FROM clan_registry WHERE inactive_reason IS NULL;",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new KnownClan(reader.GetInt64(0), reader.GetInt64(1)));
        }

        return rows;
    }

    public async Task ApplyClanScanAsync(ClanScanItem item, ClanScanDisposition disposition, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        if (disposition == ClanScanDisposition.Inserted)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO clan_registry(clan_id, owner_id) VALUES (@id, @owner)
                ON CONFLICT (clan_id) DO UPDATE
                SET owner_id = EXCLUDED.owner_id,
                    inactive_reason = NULL,
                    updated_at = now();
                INSERT INTO clan_settings(clan_id) VALUES (@id) ON CONFLICT (clan_id) DO NOTHING;
                """, connection);
            insert.Parameters.AddWithValue("id", item.ClanId);
            insert.Parameters.AddWithValue("owner", item.OwnerId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (disposition == ClanScanDisposition.OwnerReplaced)
        {
            await using var update = new NpgsqlCommand("""
                UPDATE clan_registry
                SET owner_id = @owner, inactive_reason = NULL, updated_at = now()
                WHERE clan_id = @id;
                DELETE FROM clan_admin WHERE clan_id = @id;
                UPDATE clan_settings SET version = version + 1 WHERE clan_id = @id;
                """, connection);
            update.Parameters.AddWithValue("id", item.ClanId);
            update.Parameters.AddWithValue("owner", item.OwnerId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (disposition == ClanScanDisposition.Unchanged)
        {
            await using var active = new NpgsqlCommand(
                "UPDATE clan_registry SET inactive_reason = NULL, updated_at = now() WHERE clan_id = @id;",
                connection);
            active.Parameters.AddWithValue("id", item.ClanId);
            await active.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task MarkMissingClansInactiveAsync(
        IReadOnlyCollection<long> listedClanIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE clan_registry
            SET inactive_reason = 'not returned by complete discovery',
                updated_at = now()
            WHERE clan_id <> ALL(@ids);
            """, connection);
        command.Parameters.AddWithValue("ids", listedClanIds.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkDiscoveryIncompleteAsync(bool incomplete, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("UPDATE bot_flags SET discovery_incomplete = @flag WHERE id = 1;", connection);
        command.Parameters.AddWithValue("flag", incomplete);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

