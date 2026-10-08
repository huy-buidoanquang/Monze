using Monze.Testing;

namespace Monze.Tests;

internal static class PostgresTestConfiguration
{
    // Database tests only ever use the guarded campaign PostgreSQL from
    // MONZE_TEST_POSTGRES. appsettings files hold the development database
    // and real secrets, so they are never read here.
    public static string? ReadConnectionString() => TestPostgres.ConnectionString;
}
