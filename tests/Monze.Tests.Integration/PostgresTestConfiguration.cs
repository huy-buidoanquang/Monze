using Monze.Infrastructure.Persistence;
using Monze.Testing;
using Npgsql;

namespace Monze.Tests;

internal static class PostgresTestConfiguration
{
    // Database tests only ever use the guarded campaign PostgreSQL from
    // MONZE_TEST_POSTGRES. appsettings files hold the development database
    // and real secrets, so they are never read here. The schema is applied
    // once per test process by the production migrator.
    private static readonly Lazy<string> Migrated = new(() =>
    {
        var connectionString = TestPostgres.ConnectionString;
        PostgresMigrator.EnsureDatabaseAsync(connectionString, CancellationToken.None).GetAwaiter().GetResult();
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        PostgresMigrator.ApplyAsync(dataSource, connectionString, CancellationToken.None).GetAwaiter().GetResult();
        return connectionString;
    });

    public static string? ReadConnectionString() => Migrated.Value;
}
