using Monze.Infrastructure.Persistence;
using Monze.Testing.Postgres;
using Npgsql;

namespace Monze.Campaign.Component;

/// <summary>
/// What a component scenario runs against: the guarded campaign PostgreSQL
/// servers and Redis (null when the campaign did not start them) and the
/// profile scale (Quick runs smaller sizes than Full).
/// </summary>
public sealed record ComponentContext(
    string Server,
    string? AlternateServer,
    string? Redis,
    bool Full,
    int Seed)
{
    /// <summary>
    /// A freshly migrated throwaway database and a data source with the
    /// production pool settings of MonzeHostComposition.
    /// </summary>
    public async Task<(CampaignDatabase Database, NpgsqlDataSource DataSource)> CreateDatabaseAsync(string tag, string? server = null)
    {
        var database = await CampaignDatabase.CreateEmptyAsync(server ?? Server, tag);
        await using (var migrator = NpgsqlDataSource.Create(database.ConnectionString))
        {
            await PostgresMigrator.ApplyAsync(migrator, database.ConnectionString, CancellationToken.None);
        }

        return (database, ProductionDataSource(database.ConnectionString));
    }

    public static NpgsqlDataSource ProductionDataSource(string connectionString)
        => NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 64,
            MinPoolSize = 4,
            Timeout = 5,
            CommandTimeout = 15
        }.ConnectionString);

    public static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> CountAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
