using Monze.Domain;
using Monze.Infrastructure.Persistence;
using Npgsql;
using Monze.Testing;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresRoleRepositoryTests
{
    [DbFact]
    public async Task Role_rules_honor_enabled_state_and_concurrent_grants_are_idempotent()
    {
        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var clanId = -Random.Shared.NextInt64(1, long.MaxValue);
        var ownerId = Random.Shared.NextInt64(1, long.MaxValue);
        var roleId = Random.Shared.NextInt64(1, long.MaxValue);
        var userId = Random.Shared.NextInt64(1, long.MaxValue);

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        await SeedClanAsync(dataSource, clanId, ownerId);
        var repository = new PostgresRoleRepository(dataSource);
        try
        {
            Assert.True(await repository.SetRoleRuleAsync(
                clanId,
                ownerId,
                roleId,
                RoleRuleKind.OnJoin,
                null,
                CancellationToken.None));
            Assert.Single(await repository.ListEnabledRoleRulesAsync(clanId, CancellationToken.None));

            Assert.True(await repository.SetRoleAutomationEnabledAsync(
                clanId,
                ownerId,
                false,
                CancellationToken.None));
            Assert.Empty(await repository.ListEnabledRoleRulesAsync(clanId, CancellationToken.None));

            var writes = new Task[16];
            for (var i = 0; i < writes.Length; i++)
            {
                writes[i] = repository.RecordRoleGrantAsync(
                    clanId,
                    roleId,
                    userId,
                    CancellationToken.None);
            }
            await Task.WhenAll(writes);

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var count = new NpgsqlCommand("""
                SELECT count(*) FROM role_grant
                WHERE clan_id = @clan AND role_id = @role AND user_id = @user;
                """, connection);
            count.Parameters.AddWithValue("clan", clanId);
            count.Parameters.AddWithValue("role", roleId);
            count.Parameters.AddWithValue("user", userId);
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync() ?? 0L));
        }
        finally
        {
            await CleanupClanAsync(dataSource, clanId);
        }
    }

    private static async Task SeedClanAsync(NpgsqlDataSource dataSource, long clanId, long ownerId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO clan_registry(clan_id, owner_id) VALUES (@clan, @owner);
            INSERT INTO clan_settings(clan_id, role_enabled) VALUES (@clan, TRUE);
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("owner", ownerId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupClanAsync(NpgsqlDataSource dataSource, long clanId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            DELETE FROM role_grant WHERE clan_id = @clan;
            DELETE FROM role_rule WHERE clan_id = @clan;
            DELETE FROM clan_settings WHERE clan_id = @clan;
            DELETE FROM clan_registry WHERE clan_id = @clan;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        await command.ExecuteNonQueryAsync();
    }
}
