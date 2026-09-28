using Monze.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Monze.Tests;

public sealed class PostgresMeetingSqlContractTests
{
    [Fact]
    public async Task Meeting_bind_queries_parse_against_configured_postgres()
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var query in new[]
        {
            PostgresMeetingQueries.BindMeetingRoom,
            PostgresMeetingQueries.BindMeetingRoomByVoiceChannel
        })
        {
            await using var command = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + query, connection);
            command.Parameters.AddWithValue("voice", 2104292344168714240L);
            command.Parameters.AddWithValue("clan", 2104288434238525440L);
            command.Parameters.AddWithValue("room", "contract-parse-only");
            _ = await command.ExecuteScalarAsync();
        }
    }

}
