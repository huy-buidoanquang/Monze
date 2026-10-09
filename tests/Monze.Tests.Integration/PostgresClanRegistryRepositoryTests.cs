using Monze.Domain;
using Monze.Infrastructure.Persistence;
using Npgsql;
using Monze.Testing;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresClanRegistryRepositoryTests
{
    [DbFact]
    public async Task Owner_replacement_clears_delegates_and_invalidates_settings()
    {
        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var clanId = -Random.Shared.NextInt64(1, long.MaxValue);
        var oldOwnerId = Random.Shared.NextInt64(1, long.MaxValue);
        var newOwnerId = Random.Shared.NextInt64(1, long.MaxValue);
        var adminId = Random.Shared.NextInt64(1, long.MaxValue);

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        await SeedClanAsync(dataSource, clanId, oldOwnerId, adminId);
        var repository = new PostgresClanRegistryRepository(dataSource);
        try
        {
            await repository.ApplyClanScanAsync(
                new ClanScanItem(clanId, newOwnerId),
                ClanScanDisposition.OwnerReplaced,
                CancellationToken.None);

            var state = await ReadStateAsync(dataSource, clanId);
            Assert.Equal(newOwnerId, state.OwnerId);
            Assert.Equal(0L, state.AdminCount);
            Assert.Equal(6L, state.SettingsVersion);
        }
        finally
        {
            await CleanupClanAsync(dataSource, clanId);
        }
    }

    private static async Task SeedClanAsync(NpgsqlDataSource dataSource, long clanId, long ownerId, long adminId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO clan_registry(clan_id, owner_id) VALUES (@clan, @owner);
            INSERT INTO clan_settings(clan_id, version) VALUES (@clan, 5);
            INSERT INTO clan_admin(clan_id, user_id) VALUES (@clan, @admin);
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.AddWithValue("admin", adminId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(long OwnerId, long AdminCount, long SettingsVersion)> ReadStateAsync(
        NpgsqlDataSource dataSource,
        long clanId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT c.owner_id,
                   (SELECT count(*) FROM clan_admin a WHERE a.clan_id = c.clan_id),
                   s.version
            FROM clan_registry c
            JOIN clan_settings s ON s.clan_id = c.clan_id
            WHERE c.clan_id = @clan;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task CleanupClanAsync(NpgsqlDataSource dataSource, long clanId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            DELETE FROM clan_admin WHERE clan_id = @clan;
            DELETE FROM clan_settings WHERE clan_id = @clan;
            DELETE FROM clan_registry WHERE clan_id = @clan;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        await command.ExecuteNonQueryAsync();
    }
}
