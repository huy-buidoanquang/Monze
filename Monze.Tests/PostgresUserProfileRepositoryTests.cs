using Monze.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresUserProfileRepositoryTests
{
    [Fact]
    public async Task Profile_upsert_is_clan_scoped_and_preserves_display_priority()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MONZE_RUN_DB_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        const long clanId = 2104288434238525440L;
        var userId = Random.Shared.NextInt64(1, long.MaxValue);
        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresUserProfileRepository(dataSource);
        try
        {
            await repository.UpsertAsync(
                clanId,
                userId,
                "Clan Nick",
                "Display Name",
                "username",
                "https://cdn.example/avatar.png",
                CancellationToken.None);

            var stored = await repository.GetByIdAsync(clanId, userId, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal("Clan Nick", stored!.Label);
            Assert.Equal("https://cdn.example/avatar.png", stored.AvatarUrl);

            var byUsername = await repository.FindByUsernameAsync(
                clanId,
                "USERNAME",
                CancellationToken.None);
            Assert.Equal(userId, byUsername?.UserId);

            await repository.UpsertAsync(
                clanId,
                userId,
                null,
                null,
                null,
                null,
                CancellationToken.None);

            var afterAvatarRemoval = await repository.GetByIdAsync(
                clanId,
                userId,
                CancellationToken.None);
            Assert.NotNull(afterAvatarRemoval);
            Assert.Null(afterAvatarRemoval!.AvatarUrl);
            Assert.Equal("Clan Nick", afterAvatarRemoval.Label);

            var otherClan = await repository.GetByIdAsync(
                clanId + 1,
                userId,
                CancellationToken.None);
            Assert.Null(otherClan);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "DELETE FROM clan_user_profile WHERE clan_id = @clan AND user_id = @user;",
                connection);
            command.Parameters.AddWithValue("clan", clanId);
            command.Parameters.AddWithValue("user", userId);
            await command.ExecuteNonQueryAsync();
        }
    }
}
