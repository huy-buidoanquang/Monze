using Monze.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresAiUsageRepositoryTests
{
    [Fact]
    public async Task Concurrent_budget_consumption_never_exceeds_the_daily_cap()
    {
        if (!PostgresTestEnabled())
        {
            return;
        }

        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var clanId = -Random.Shared.NextInt64(1, long.MaxValue);
        var userId = Random.Shared.NextInt64(1, long.MaxValue);
        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresAiUsageRepository(dataSource);
        try
        {
            var attempts = new Task<(bool Allowed, int Used)>[20];
            for (var i = 0; i < attempts.Length; i++)
            {
                attempts[i] = repository.ConsumeAiAsync(
                    clanId,
                    userId,
                    10,
                    100,
                    CancellationToken.None);
            }

            var results = await Task.WhenAll(attempts);
            Assert.Equal(10, results.Count(static result => result.Allowed));
            Assert.All(results.Where(static result => result.Allowed), static result => Assert.InRange(result.Used, 10, 100));

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT tokens FROM ai_usage
                WHERE clan_id = @clan AND user_id = @user AND usage_day = CURRENT_DATE;
                """, connection);
            command.Parameters.AddWithValue("clan", clanId);
            command.Parameters.AddWithValue("user", userId);
            Assert.Equal(100, (int)(await command.ExecuteScalarAsync() ?? 0));

            var rejected = await repository.ConsumeAiAsync(
                clanId,
                userId,
                1,
                100,
                CancellationToken.None);
            Assert.False(rejected.Allowed);
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand(
                "DELETE FROM ai_usage WHERE clan_id = @clan AND user_id = @user;",
                connection);
            cleanup.Parameters.AddWithValue("clan", clanId);
            cleanup.Parameters.AddWithValue("user", userId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static bool PostgresTestEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable("MONZE_RUN_DB_TESTS"),
            "1",
            StringComparison.Ordinal);
}
