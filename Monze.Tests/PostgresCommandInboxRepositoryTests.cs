using Monze.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresCommandInboxRepositoryTests
{
    [Fact]
    public async Task Command_inbox_claim_is_idempotent_and_lease_scoped()
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
        const long channelId = 2104288438869037056L;
        var firstMessageId = -Random.Shared.NextInt64(1, long.MaxValue);
        var secondMessageId = firstMessageId == long.MinValue
            ? long.MinValue + 1
            : firstMessageId - 1;
        var concurrentMessageId = secondMessageId == long.MinValue
            ? long.MinValue + 1
            : secondMessageId - 1;
        var uncertainMessageId = concurrentMessageId == long.MinValue
            ? long.MinValue + 1
            : concurrentMessageId - 1;

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresCommandInboxRepository(dataSource);
        try
        {
            var first = await repository.TryClaimAsync(
                clanId,
                channelId,
                firstMessageId,
                CancellationToken.None);
            Assert.True(first.HasValue);

            var duplicate = await repository.TryClaimAsync(
                clanId,
                channelId,
                firstMessageId,
                CancellationToken.None);
            Assert.False(duplicate.HasValue);

            var concurrentClaims = await Task.WhenAll(
                repository.TryClaimAsync(clanId, channelId, concurrentMessageId, CancellationToken.None),
                repository.TryClaimAsync(clanId, channelId, concurrentMessageId, CancellationToken.None));
            Assert.Equal(1, concurrentClaims.Count(static claim => claim.HasValue));

            Assert.True(await repository.CompleteAsync(first!.Value, CancellationToken.None));
            var completedDuplicate = await repository.TryClaimAsync(
                clanId,
                channelId,
                firstMessageId,
                CancellationToken.None);
            Assert.False(completedDuplicate.HasValue);

            var second = await repository.TryClaimAsync(
                clanId,
                channelId,
                secondMessageId,
                CancellationToken.None);
            Assert.True(second.HasValue);

            var wrongLease = second!.Value with { Token = "wrong-lease" };
            await repository.ReleaseAsync(wrongLease, CancellationToken.None);
            Assert.False((await repository.TryClaimAsync(
                clanId,
                channelId,
                secondMessageId,
                CancellationToken.None)).HasValue);

            await repository.ReleaseAsync(second.Value, CancellationToken.None);
            Assert.True((await repository.TryClaimAsync(
                clanId,
                channelId,
                secondMessageId,
                CancellationToken.None)).HasValue);

            var uncertain = await repository.TryClaimAsync(
                clanId,
                channelId,
                uncertainMessageId,
                CancellationToken.None);
            Assert.True(uncertain.HasValue);
            Assert.True(await repository.MarkUncertainAsync(
                uncertain!.Value,
                CancellationToken.None));
            await ExpireLeaseAsync(dataSource, clanId, channelId, uncertainMessageId);
            Assert.False((await repository.TryClaimAsync(
                clanId,
                channelId,
                uncertainMessageId,
                CancellationToken.None)).HasValue);
        }
        finally
        {
            await using var cleanup = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                DELETE FROM command_inbox
                WHERE clan_id = @clan
                  AND channel_id = @channel
                  AND message_id = ANY(@messages);
                """, cleanup);
            command.Parameters.AddWithValue("clan", clanId);
            command.Parameters.AddWithValue("channel", channelId);
            command.Parameters.AddWithValue("messages", new[] { firstMessageId, secondMessageId, concurrentMessageId, uncertainMessageId });
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task ExpireLeaseAsync(
        NpgsqlDataSource dataSource,
        long clanId,
        long channelId,
        long messageId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE command_inbox
            SET locked_until = now() - interval '1 minute'
            WHERE clan_id = @clan AND channel_id = @channel AND message_id = @message;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.AddWithValue("message", messageId);
        await command.ExecuteNonQueryAsync();
    }
}
