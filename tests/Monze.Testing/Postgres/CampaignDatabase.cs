using Npgsql;

namespace Monze.Testing.Postgres;

/// <summary>
/// A throwaway database on a campaign server: created empty or cloned from a
/// template, dropped (WITH FORCE) on dispose. Names always start with
/// "monze_t_" and every connection string passes the campaign guard, so this
/// can never create or drop a database outside a campaign container.
/// </summary>
public sealed class CampaignDatabase : IAsyncDisposable
{
    private static readonly SemaphoreSlim CloneGate = new(1, 1);
    private readonly string _server;

    private CampaignDatabase(string server, string name)
    {
        _server = server;
        Name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
        TestPostgres.AssertCampaignDatabase(ConnectionString);
    }

    public string Name { get; }

    public string ConnectionString { get; }

    public static async Task<CampaignDatabase> CreateEmptyAsync(string server, string tag)
    {
        var database = new CampaignDatabase(server, NewName(tag));
        await ExecuteAsync(server, $"CREATE DATABASE {Quote(database.Name)};");
        return database;
    }

    /// <summary>Clones <paramref name="template"/>; clones are serialized because a template must have no other users.</summary>
    public static async Task<CampaignDatabase> CloneAsync(string server, string template, string tag)
    {
        var database = new CampaignDatabase(server, NewName(tag));
        await CloneGate.WaitAsync();
        try
        {
            await ExecuteAsync(server, $"CREATE DATABASE {Quote(database.Name)} TEMPLATE {Quote(template)};");
        }
        finally
        {
            CloneGate.Release();
        }

        return database;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await ExecuteAsync(_server, $"DROP DATABASE IF EXISTS {Quote(Name)} WITH (FORCE);");
    }

    /// <summary>Runs a statement on the server's maintenance database without pooling.</summary>
    public static async Task ExecuteAsync(string server, string sql)
    {
        TestPostgres.AssertCampaignDatabase(server);
        var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static string Quote(string name)
    {
        if (!name.StartsWith(TestPostgres.DatabaseNamePrefix, StringComparison.Ordinal)
            || name.Any(static ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_')))
        {
            throw new InvalidOperationException($"Refusing database name '{name}'.");
        }

        return "\"" + name + "\"";
    }

    private static string NewName(string tag)
    {
        var safe = new string(tag.ToLowerInvariant().Select(static ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').ToArray());
        var name = $"monze_t_{safe}_{Guid.NewGuid():N}";
        return name.Length <= 63 ? name : name[..63];
    }
}
