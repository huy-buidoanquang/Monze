using Monze.Domain;
using Monze.Infrastructure.Persistence;
using Npgsql;
using Monze.Testing;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresSchedulingRepositoryTests
{
    [DbFact]
    public async Task Schedule_list_and_cancel_are_scoped_to_clan_channel_and_requester()
    {
        var connectionString = PostgresTestConfiguration.ReadConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        // A synthetic clan keeps the test independent of any real clan data.
        var clanId = -Random.Shared.NextInt64(1, long.MaxValue / 2);
        var channelId = clanId - 1;
        var ownerId = Random.Shared.NextInt64(1, long.MaxValue);
        var requesterId = -Random.Shared.NextInt64(1, long.MaxValue);
        var otherUserId = requesterId == long.MinValue ? long.MinValue + 1 : requesterId - 1;

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresSchedulingRepository(dataSource);
        await using (var seedConnection = await dataSource.OpenConnectionAsync())
        await using (var seedCommand = new NpgsqlCommand(
            "INSERT INTO clan_registry(clan_id, owner_id) VALUES (@clan, @owner);",
            seedConnection))
        {
            seedCommand.Parameters.AddWithValue("clan", clanId);
            seedCommand.Parameters.AddWithValue("owner", ownerId);
            await seedCommand.ExecuteNonQueryAsync();
        }

        var scheduleId = await repository.CreateMeetingScheduleAsync(
            clanId,
            channelId,
            requesterId,
            "Contract Review",
            MeetingScheduleKind.Once,
            "29/09/2026 18:30",
            "Asia/Ho_Chi_Minh",
            DateTimeOffset.UtcNow.AddHours(2),
            CancellationToken.None);

        try
        {
            var own = await repository.ListMeetingSchedulesAsync(
                clanId,
                channelId,
                requesterId,
                20,
                CancellationToken.None);
            Assert.Contains(own, item =>
                item.Id == scheduleId
                && item.Name == "Contract Review"
                && item.Kind == MeetingScheduleKind.Once);

            var other = await repository.ListMeetingSchedulesAsync(
                clanId,
                channelId,
                otherUserId,
                20,
                CancellationToken.None);
            Assert.DoesNotContain(other, item => item.Id == scheduleId);
            Assert.False(await repository.CancelMeetingScheduleAsync(
                clanId,
                channelId,
                otherUserId,
                scheduleId,
                CancellationToken.None));

            var ownerSchedules = await repository.ListMeetingSchedulesAsync(
                clanId,
                channelId,
                ownerId,
                20,
                CancellationToken.None);
            Assert.Contains(ownerSchedules, item => item.Id == scheduleId);

            Assert.True(await repository.CancelMeetingScheduleAsync(
                clanId,
                channelId,
                ownerId,
                scheduleId,
                CancellationToken.None));

            Assert.False(await repository.CancelMeetingScheduleAsync(
                clanId,
                channelId,
                requesterId,
                scheduleId,
                CancellationToken.None));
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "DELETE FROM meeting_schedule WHERE id = @id; DELETE FROM clan_registry WHERE clan_id = @clan;",
                connection);
            command.Parameters.AddWithValue("id", scheduleId);
            command.Parameters.AddWithValue("clan", clanId);
            await command.ExecuteNonQueryAsync();
        }
    }
}
