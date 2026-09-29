using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Monze.Infrastructure.Persistence;

public static class PostgresMigrator
{
    public const long LockKey = 814214;

    public static async Task ValidateAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var table = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_class AS c
                JOIN pg_namespace AS n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = 'schema_migrations');
            """,
            connection);
        var schemaExists = (bool?)await table.ExecuteScalarAsync(cancellationToken) ?? false;
        if (!schemaExists)
        {
            throw new InvalidOperationException(
                "Monze schema is missing. Run 'Monze migrate' before starting the bot.");
        }

        var assembly = typeof(PostgresMigrator).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken);
            var version = Path.GetFileNameWithoutExtension(resource);
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            await using var check = new NpgsqlCommand(
                "SELECT checksum FROM schema_migrations WHERE version = @v;",
                connection);
            check.Parameters.AddWithValue("v", version);
            var existing = await check.ExecuteScalarAsync(cancellationToken) as string;
            if (!string.Equals(existing, checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Migration {version} is missing or has a checksum mismatch.");
            }
        }
    }

    public static async Task ApplyAsync(NpgsqlDataSource dataSource, string connectionString, CancellationToken cancellationToken)
    {
        await EnsureDatabaseAsync(connectionString, cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(@key);", connection))
        {
            lockCommand.Parameters.AddWithValue("key", LockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await Execute(connection, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                  version TEXT PRIMARY KEY,
                  checksum TEXT NOT NULL,
                  applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
                );
                """, null, cancellationToken);

            var assembly = typeof(PostgresMigrator).Assembly;
            var resources = assembly.GetManifestResourceNames()
                .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            foreach (var resource in resources)
            {
                await using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                var sql = await reader.ReadToEndAsync(cancellationToken);
                var version = Path.GetFileNameWithoutExtension(resource);
                var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
                await using var check = new NpgsqlCommand("SELECT checksum FROM schema_migrations WHERE version = @v;", connection);
                check.Parameters.AddWithValue("v", version);
                var existing = await check.ExecuteScalarAsync(cancellationToken) as string;
                if (existing is not null)
                {
                    if (!string.Equals(existing, checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Migration {version} checksum changed after it was applied.");
                    }

                    continue;
                }

                await using var tx = await connection.BeginTransactionAsync(cancellationToken);
                await Execute(connection, sql, tx, cancellationToken);
                await using var mark = new NpgsqlCommand("INSERT INTO schema_migrations(version, checksum) VALUES (@v, @c);", connection, tx);
                mark.Parameters.AddWithValue("v", version);
                mark.Parameters.AddWithValue("c", checksum);
                await mark.ExecuteNonQueryAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key);", connection);
            unlock.Parameters.AddWithValue("key", LockKey);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    public static async Task EnsureDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database;
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException("Monze:Postgres must include a Database name.");
        }

        if (string.Equals(database, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        builder.Database = "postgres";
        builder.PersistSecurityInfo = true;
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name;", connection);
        exists.Parameters.AddWithValue("name", database);
        if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
        {
            return;
        }

        await using var create = new NpgsqlCommand($"CREATE DATABASE {QuoteIdentifier(database)};", connection);
        try
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
        }
    }

    internal static string QuoteIdentifier(string name)
    {
        if (name.Contains('\0'))
        {
            throw new InvalidOperationException("Database name contains a null character.");
        }

        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, NpgsqlTransaction? tx, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
