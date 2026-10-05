using Monze.Application;
using Monze.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresWelcomeRepositoryTests
{
    [Fact]
    public async Task Welcome_configuration_and_delivery_claim_are_durable_and_clan_scoped()
    {
        if (!PostgresTestEnabled())
        {
            return;
        }

        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var clanId = -Random.Shared.NextInt64(1, long.MaxValue);
        var ownerId = Random.Shared.NextInt64(1, long.MaxValue);
        var adminId = Random.Shared.NextInt64(1, long.MaxValue);
        var memberId = Random.Shared.NextInt64(1, long.MaxValue);

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        await SeedClanAsync(dataSource, clanId, ownerId, adminId);
        var repository = new PostgresWelcomeRepository(dataSource);
        try
        {
            Assert.True(await repository.SetWelcomeMessageAsync(
                clanId,
                ownerId,
                "Chào {user}",
                CancellationToken.None) > 0);

            var embed = new WelcomeEmbedSettings(Title: "Xin chào", Description: "Nội dung");
            Assert.True(await repository.SetWelcomeConfigurationAsync(
                clanId,
                adminId,
                true,
                null,
                embed,
                CancellationToken.None) > 0);

            var settings = await repository.GetWelcomeAsync(clanId, CancellationToken.None);
            Assert.NotNull(settings);
            Assert.True(settings!.Enabled);
            Assert.Equal("Chào {user}", settings.Text);
            Assert.Equal(embed, settings.Embed);

            var claims = await Task.WhenAll(
                repository.TryClaimWelcomeAsync(clanId, memberId, CancellationToken.None),
                repository.TryClaimWelcomeAsync(clanId, memberId, CancellationToken.None));
            Assert.Equal(1, claims.Count(static claimed => claimed));

            await repository.ReleaseWelcomeClaimAsync(clanId, memberId, CancellationToken.None);
            Assert.True(await repository.TryClaimWelcomeAsync(clanId, memberId, CancellationToken.None));
        }
        finally
        {
            await CleanupClanAsync(dataSource, clanId);
        }
    }

    private static bool PostgresTestEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable("MONZE_RUN_DB_TESTS"),
            "1",
            StringComparison.Ordinal);

    private static async Task SeedClanAsync(NpgsqlDataSource dataSource, long clanId, long ownerId, long adminId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO clan_registry(clan_id, owner_id) VALUES (@clan, @owner);
            INSERT INTO clan_settings(clan_id) VALUES (@clan);
            INSERT INTO clan_admin(clan_id, user_id) VALUES (@clan, @admin);
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.AddWithValue("admin", adminId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupClanAsync(NpgsqlDataSource dataSource, long clanId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            DELETE FROM welcome_delivery WHERE clan_id = @clan;
            DELETE FROM clan_admin WHERE clan_id = @clan;
            DELETE FROM clan_settings WHERE clan_id = @clan;
            DELETE FROM clan_registry WHERE clan_id = @clan;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        await command.ExecuteNonQueryAsync();
    }
}
