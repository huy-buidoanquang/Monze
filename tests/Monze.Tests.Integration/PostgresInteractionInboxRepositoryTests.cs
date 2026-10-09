using Monze.Infrastructure.Persistence;
using Npgsql;
using Monze.Testing;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresInteractionInboxRepositoryTests
{
    [DbFact]
    public async Task Interaction_claim_is_action_scoped_concurrent_and_fail_closed_after_terminal_state()
    {
        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var clanId = -Random.Shared.NextInt64(1, long.MaxValue);
        var channelId = -Random.Shared.NextInt64(1, long.MaxValue);
        var messageId = -Random.Shared.NextInt64(1, long.MaxValue);
        const string startAction = "monze_meeting_now";
        const string submitAction = "monze_meeting_schedule_submit";

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresInteractionInboxRepository(dataSource);
        try
        {
            var concurrent = await Task.WhenAll(
                repository.TryClaimAsync(clanId, channelId, messageId, startAction, CancellationToken.None),
                repository.TryClaimAsync(clanId, channelId, messageId, startAction, CancellationToken.None));
            var firstClaim = Assert.Single(concurrent, static claim => claim.HasValue);
            var first = firstClaim.GetValueOrDefault();

            Assert.Null(await repository.TryClaimAsync(
                clanId,
                channelId,
                messageId,
                startAction,
                CancellationToken.None));

            var otherAction = await repository.TryClaimAsync(
                clanId,
                channelId,
                messageId,
                submitAction,
                CancellationToken.None);
            Assert.True(otherAction.HasValue);

            Assert.False(await repository.CompleteAsync(
                first with { Token = "wrong-lease" },
                CancellationToken.None));
            Assert.True(await repository.CompleteAsync(first, CancellationToken.None));
            Assert.Null(await repository.TryClaimAsync(
                clanId,
                channelId,
                messageId,
                startAction,
                CancellationToken.None));

            await repository.ReleaseAsync(otherAction!.Value, CancellationToken.None);
            var retry = await repository.TryClaimAsync(
                clanId,
                channelId,
                messageId,
                submitAction,
                CancellationToken.None);
            Assert.True(retry.HasValue);
            Assert.True(await repository.MarkUncertainAsync(retry.Value, CancellationToken.None));
            Assert.Null(await repository.TryClaimAsync(
                clanId,
                channelId,
                messageId,
                submitAction,
                CancellationToken.None));
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM interaction_inbox
                WHERE clan_id = @clan
                  AND channel_id = @channel
                  AND message_id = @message;
                """, connection);
            cleanup.Parameters.AddWithValue("clan", clanId);
            cleanup.Parameters.AddWithValue("channel", channelId);
            cleanup.Parameters.AddWithValue("message", messageId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
