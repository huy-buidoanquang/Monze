using System.Security.Cryptography;
using System.Text;
using Monze.Infrastructure.Persistence;
using Monze.Testing.Postgres;
using Npgsql;

namespace Monze.Tests.Migrations;

/// <summary>
/// The embedded migrations in the order PostgresMigrator applies them, and a
/// way to apply only the first k with the same bookkeeping (version = resource
/// name without ".sql", checksum = SHA-256 of the UTF-8 text), so the real
/// migrator can finish the rest.
/// </summary>
internal static class MigrationSteps
{
    public static IReadOnlyList<(string Version, string Name, string Sql)> All { get; } = Load();

    public static int IndexOf(string shortName)
        => All.Select(static (step, i) => (step.Name, i)).Single(pair => pair.Name.StartsWith(shortName, StringComparison.Ordinal)).i;

    public static async Task ApplyFirstAsync(string connectionString, int count)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
              version TEXT PRIMARY KEY,
              checksum TEXT NOT NULL,
              applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            """);
        foreach (var (version, _, sql) in All.Take(count))
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await Execute(connection, transaction, sql);
            await using var mark = new NpgsqlCommand("INSERT INTO schema_migrations(version, checksum) VALUES (@v, @c);", connection, transaction);
            mark.Parameters.AddWithValue("v", version);
            mark.Parameters.AddWithValue("c", Checksum(sql));
            await mark.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
    }

    public static async Task ApplyAllAsync(string connectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await PostgresMigrator.ApplyAsync(dataSource, connectionString, CancellationToken.None);
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await Execute(connection, null, sql);
    }

    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<IReadOnlyList<string>> RowsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    public static string Checksum(string sql) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static IReadOnlyList<(string Version, string Name, string Sql)> Load()
    {
        var assembly = typeof(PostgresMigrator).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(static name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(resource =>
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                var version = Path.GetFileNameWithoutExtension(resource);
                return (version, version[(version.LastIndexOf('.') + 1)..], reader.ReadToEnd());
            })
            .ToList();
    }
}

/// <summary>
/// One migrated template database per campaign server, built from an empty
/// database by the production migrator; tests clone it instead of migrating.
/// </summary>
internal static class MigrationTemplate
{
    public const string Name = "monze_t_template";

    private static readonly Dictionary<string, Lazy<Task<IReadOnlyList<string>>>> Templates = new(StringComparer.Ordinal);

    /// <summary>Creates the template on first use and returns its schema fingerprint.</summary>
    public static Task<IReadOnlyList<string>> EnsureAsync(string server)
    {
        lock (Templates)
        {
            if (!Templates.TryGetValue(server, out var template))
            {
                template = new Lazy<Task<IReadOnlyList<string>>>(() => CreateAsync(server));
                Templates[server] = template;
            }

            return template.Value;
        }
    }

    private static async Task<IReadOnlyList<string>> CreateAsync(string server)
    {
        await CampaignDatabase.ExecuteAsync(server, $"DROP DATABASE IF EXISTS {CampaignDatabase.Quote(Name)} WITH (FORCE);");
        await CampaignDatabase.ExecuteAsync(server, $"CREATE DATABASE {CampaignDatabase.Quote(Name)};");
        var connectionString = new NpgsqlConnectionStringBuilder(server) { Database = Name, Pooling = false }.ConnectionString;
        await MigrationSteps.ApplyAllAsync(connectionString);
        var fingerprint = await SchemaFingerprint.LinesAsync(connectionString);
        NpgsqlConnection.ClearAllPools();
        return fingerprint;
    }
}
