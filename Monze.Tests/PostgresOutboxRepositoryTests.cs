using Monze.Domain;
using Monze.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresOutboxRepositoryTests
{
    [Fact]
    public async Task Outbox_claim_completion_reclaim_and_uncertain_resend_are_lease_scoped()
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
        var firstKey = $"outbox-test:{Guid.NewGuid():N}";
        var uncertainKey = $"outbox-test:{Guid.NewGuid():N}";

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresOutboxRepository(dataSource);
        try
        {
            await repository.EnqueueAsync(
                clanId,
                channelId,
                OutboxKind.CommandReply,
                firstKey,
                "outbox integration first",
                CancellationToken.None);

            var firstId = await ReadIdAsync(firstKey);
            var claims = await Task.WhenAll(
                repository.ClaimDueOutboxAsync(CancellationToken.None, clanId),
                repository.ClaimDueOutboxAsync(CancellationToken.None, clanId));
            var firstClaims = claims
                .SelectMany(static batch => batch)
                .Where(item => item.Id == firstId)
                .ToArray();

            Assert.Single(firstClaims);
            var firstLease = firstClaims[0];

            await repository.CompleteOutboxAsync(
                firstId,
                "wrong-lease",
                null,
                failed: true,
                CancellationToken.None,
                "wrong-token");

            var afterWrongCompletion = await ReadStateAsync(firstId);
            Assert.Equal("sending", afterWrongCompletion.Status);
            Assert.Equal(0, afterWrongCompletion.Attempts);
            Assert.Equal(firstLease.LeaseToken, afterWrongCompletion.LeaseToken);

            await repository.CompleteOutboxAsync(
                firstId,
                firstLease.LeaseToken,
                null,
                failed: false,
                CancellationToken.None,
                "temporary-failure");

            var afterRetryableCompletion = await ReadStateAsync(firstId);
            Assert.Equal("pending", afterRetryableCompletion.Status);
            Assert.Equal(1, afterRetryableCompletion.Attempts);
            Assert.Null(afterRetryableCompletion.ExternalMessageId);
            Assert.Equal("temporary-failure", afterRetryableCompletion.LastError);
            Assert.True(afterRetryableCompletion.DueAt > DateTimeOffset.UtcNow);
            Assert.DoesNotContain(
                (await repository.ClaimDueOutboxAsync(CancellationToken.None, clanId))
                    .Select(item => item.Id),
                id => id == firstId);

            await ExecuteAsync(
                "UPDATE outbox_delivery SET due_at = now() WHERE id = @id AND clan_id = @clan;",
                command =>
                {
                    command.Parameters.AddWithValue("id", firstId);
                    command.Parameters.AddWithValue("clan", clanId);
                });

            var reclaimed = await repository.ClaimDueOutboxAsync(CancellationToken.None, clanId);
            var reclaimedFirst = Assert.Single(reclaimed, item => item.Id == firstId);
            await repository.CompleteOutboxAsync(
                firstId,
                reclaimedFirst.LeaseToken,
                991234567890L,
                failed: true,
                CancellationToken.None,
                "external-id-wins");

            var afterSuccessfulCompletion = await ReadStateAsync(firstId);
            Assert.Equal("sent", afterSuccessfulCompletion.Status);
            Assert.Equal(2, afterSuccessfulCompletion.Attempts);
            Assert.Equal(991234567890L, afterSuccessfulCompletion.ExternalMessageId);
            Assert.Null(afterSuccessfulCompletion.LastError);

            await repository.EnqueueAsync(
                clanId,
                channelId,
                OutboxKind.Announcement,
                uncertainKey,
                "outbox integration uncertain",
                CancellationToken.None);
            var uncertainId = await ReadIdAsync(uncertainKey);
            var uncertainClaim = Assert.Single(
                await repository.ClaimDueOutboxAsync(CancellationToken.None, clanId),
                item => item.Id == uncertainId);

            await repository.CompleteOutboxAsync(
                uncertainId,
                uncertainClaim.LeaseToken,
                null,
                failed: true,
                CancellationToken.None,
                "delivery-uncertain");

            var uncertainRows = await repository.ListUncertainAsync(clanId, CancellationToken.None);
            Assert.Contains(uncertainRows, row => row.StartsWith($"{uncertainId} ", StringComparison.Ordinal));

            Assert.False(await repository.ResendAsync(clanId + 1, uncertainId, CancellationToken.None));
            Assert.True(await repository.ResendAsync(clanId, uncertainId, CancellationToken.None));

            var resent = Assert.Single(
                await repository.ClaimDueOutboxAsync(CancellationToken.None, clanId),
                item => item.Id == uncertainId);
            await repository.CompleteOutboxAsync(
                uncertainId,
                resent.LeaseToken,
                991234567891L,
                failed: false,
                CancellationToken.None);

            var afterResend = await ReadStateAsync(uncertainId);
            Assert.Equal("sent", afterResend.Status);
            Assert.Equal(991234567891L, afterResend.ExternalMessageId);
            Assert.Equal(1, afterResend.Attempts);
        }
        finally
        {
            await ExecuteAsync(
                "DELETE FROM outbox_delivery WHERE dedupe_key IN (@first, @uncertain);",
                command =>
                {
                    command.Parameters.AddWithValue("first", firstKey);
                    command.Parameters.AddWithValue("uncertain", uncertainKey);
                });
        }

        async Task<long> ReadIdAsync(string dedupeKey)
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT id FROM outbox_delivery WHERE dedupe_key = @key;",
                connection);
            command.Parameters.AddWithValue("key", dedupeKey);
            return (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Outbox row was not inserted."));
        }

        async Task<(string Status, int Attempts, DateTimeOffset DueAt, long? ExternalMessageId, string? LeaseToken, string? LastError)> ReadStateAsync(long id)
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT status, attempts, due_at, external_message_id, lease_token, last_error
                FROM outbox_delivery
                WHERE id = @id;
                """, connection);
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
        }

        async Task ExecuteAsync(string sql, Action<NpgsqlCommand> configure)
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            configure(command);
            await command.ExecuteNonQueryAsync();
        }
    }
}
