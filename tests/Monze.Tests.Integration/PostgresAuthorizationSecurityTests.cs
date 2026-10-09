using Monze.Application;
using Monze.Infrastructure.Persistence;
using Npgsql;
using Monze.Testing;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresAuthorizationSecurityTests
{
    [DbFact]
    public async Task Unauthorized_write_paths_are_denied_by_database_predicates()
    {
        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        const long clanId = 2104288434238525440L;
        const long unauthorizedUserId = long.MaxValue;
        var targetUserId = Random.Shared.NextInt64(1, long.MaxValue - 1);
        var roleId = Random.Shared.NextInt64(1, long.MaxValue - 1);

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var authorization = new PostgresAuthorizationRepository(dataSource);
        var welcome = new PostgresWelcomeRepository(dataSource);
        var roles = new PostgresRoleRepository(dataSource);
        var before = await welcome.GetWelcomeAsync(clanId, CancellationToken.None);

        try
        {
            Assert.False(await authorization.SetDelegateAsync(
                clanId,
                unauthorizedUserId,
                targetUserId,
                true,
                CancellationToken.None));

            Assert.Equal(0, await welcome.SetWelcomeConfigurationAsync(
                clanId,
                unauthorizedUserId,
                before?.Enabled ?? false,
                "must-not-be-written",
                new WelcomeEmbedSettings(Title: "must-not-be-written"),
                CancellationToken.None));

            Assert.False(await roles.SetRoleAutomationEnabledAsync(
                clanId,
                unauthorizedUserId,
                false,
                CancellationToken.None));

            Assert.False(await roles.SetRoleRuleAsync(
                clanId,
                unauthorizedUserId,
                roleId,
                Monze.Domain.RoleRuleKind.OnJoin,
                null,
                CancellationToken.None));

            Assert.False(await roles.RemoveRoleRuleAsync(
                clanId,
                unauthorizedUserId,
                roleId,
                Monze.Domain.RoleRuleKind.OnJoin,
                CancellationToken.None));

            var after = await welcome.GetWelcomeAsync(clanId, CancellationToken.None);
            Assert.Equal(before, after);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                DELETE FROM role_rule
                WHERE clan_id = @clan AND role_id = @role;
                DELETE FROM clan_admin
                WHERE clan_id = @clan AND user_id = @target;
                """, connection);
            command.Parameters.AddWithValue("clan", clanId);
            command.Parameters.AddWithValue("role", roleId);
            command.Parameters.AddWithValue("target", targetUserId);
            await command.ExecuteNonQueryAsync();
        }
    }
}
