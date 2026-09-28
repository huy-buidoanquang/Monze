using Monze.Domain;
using Monze.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresCommunityRepositoryTests
{
    [Fact]
    public async Task Event_signup_serializes_capacity_and_reports_waitlist_state()
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

        var clanId = -Random.Shared.NextInt64(1, 9_000_000_000);
        var channelId = -Random.Shared.NextInt64(1, 9_000_000_000);
        var ownerId = -Random.Shared.NextInt64(1, 9_000_000_000);
        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresCommunityRepository(dataSource);
        long eventId = 0;

        try
        {
            eventId = await repository.CreateEventAsync(
                clanId,
                channelId,
                ownerId,
                "community capacity test",
                DateTimeOffset.UtcNow.AddHours(2),
                capacity: 1,
                CancellationToken.None);

            var first = await repository.JoinEventAsync(clanId, ownerId + 1, CancellationToken.None);
            var second = await repository.JoinEventAsync(clanId, ownerId + 2, CancellationToken.None);
            var replay = await repository.JoinEventAsync(clanId, ownerId + 2, CancellationToken.None);

            Assert.Equal(EventJoinStatus.Confirmed, first);
            Assert.Equal(EventJoinStatus.Waitlisted, second);
            Assert.Equal(EventJoinStatus.AlreadyRegistered, replay);

            var recap = await repository.RecapEventAsync(clanId, CancellationToken.None);
            Assert.Contains("1/1", recap, StringComparison.Ordinal);
            Assert.Contains("Danh sách chờ: 1", recap, StringComparison.Ordinal);

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT status, COUNT(*)
                FROM signup_entry
                WHERE event_id = @event
                GROUP BY status
                ORDER BY status;
                """, connection);
            command.Parameters.AddWithValue("event", eventId);
            await using var reader = await command.ExecuteReaderAsync();
            var states = new Dictionary<string, long>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                states[reader.GetString(0)] = reader.GetInt64(1);
            }

            Assert.Equal(1, states["confirmed"]);
            Assert.Equal(1, states["waitlisted"]);
        }
        finally
        {
            await using var cleanup = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                DELETE FROM signup_entry WHERE event_id = @event;
                DELETE FROM outbox_delivery WHERE dedupe_key LIKE @prefix;
                DELETE FROM community_event WHERE id = @event;
                """, cleanup);
            command.Parameters.AddWithValue("event", eventId);
            command.Parameters.AddWithValue("prefix", $"event:{eventId}:%");
            await command.ExecuteNonQueryAsync();
        }
    }
}
