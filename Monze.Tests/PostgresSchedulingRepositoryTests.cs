using Monze.Domain;
using Monze.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresSchedulingRepositoryTests
{
    [Fact]
    public async Task Schedule_list_and_cancel_are_scoped_to_clan_channel_and_requester()
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
        var requesterId = -Random.Shared.NextInt64(1, long.MaxValue);
        var otherUserId = requesterId == long.MinValue ? long.MinValue + 1 : requesterId - 1;

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var repository = new PostgresSchedulingRepository(dataSource);
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
                includeAll: false,
                limit: 20,
                CancellationToken.None);
            Assert.Contains(own, item =>
                item.Id == scheduleId
                && item.Name == "Contract Review"
                && item.Kind == MeetingScheduleKind.Once);

            var other = await repository.ListMeetingSchedulesAsync(
                clanId,
                channelId,
                otherUserId,
                includeAll: false,
                limit: 20,
                CancellationToken.None);
            Assert.DoesNotContain(other, item => item.Id == scheduleId);
            Assert.False(await repository.CancelMeetingScheduleAsync(
                clanId,
                channelId,
                otherUserId,
                includeAll: false,
                scheduleId,
                CancellationToken.None));
            Assert.True(await repository.CancelMeetingScheduleAsync(
                clanId,
                channelId,
                requesterId,
                includeAll: false,
                scheduleId,
                CancellationToken.None));
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "DELETE FROM meeting_schedule WHERE id = @id;",
                connection);
            command.Parameters.AddWithValue("id", scheduleId);
            await command.ExecuteNonQueryAsync();
        }
    }
}
