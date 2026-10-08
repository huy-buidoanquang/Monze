using Npgsql;

namespace Monze.Tests.E2E.Harness;

/// <summary>
/// Every row of every Monze business table, as JSON text, for "unauthorized
/// actors change nothing" checks. The table list is read from the migrated
/// schema at capture time, so a new table is covered automatically. Excluded
/// are bookkeeping tables that record that an input arrived rather than what
/// it changed: schema_migrations, command_inbox and interaction_inbox
/// (idempotency claims) and clan_user_profile (a cache of the sender's
/// platform profile refreshed by every incoming message).
/// </summary>
internal sealed class BusinessSnapshot
{
    public static readonly IReadOnlySet<string> ExcludedTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "schema_migrations",
        "command_inbox",
        "interaction_inbox",
        "clan_user_profile"
    };

    private BusinessSnapshot(IReadOnlyDictionary<string, IReadOnlyList<string>> tables)
    {
        Tables = tables;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Tables { get; }

    public static async Task<BusinessSnapshot> CaptureAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var names = new List<string>();
        await using (var list = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name;",
            connection))
        await using (var reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                if (!ExcludedTables.Contains(name))
                {
                    names.Add(name);
                }
            }
        }

        var tables = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            // Names come from the catalog and are plain identifiers.
            await using var rows = new NpgsqlCommand(
                $"SELECT row_to_json(t)::text FROM \"{name}\" t ORDER BY 1;",
                connection);
            var values = new List<string>();
            await using var reader = await rows.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                values.Add(reader.GetString(0));
            }

            tables[name] = values;
        }

        return new BusinessSnapshot(tables);
    }

    /// <summary>Human-readable differences (row counts and changed tables only, no row content).</summary>
    public IReadOnlyList<string> Differences(BusinessSnapshot after)
    {
        var differences = new List<string>();
        foreach (var name in Tables.Keys.Union(after.Tables.Keys).Order(StringComparer.Ordinal))
        {
            var before = Tables.GetValueOrDefault(name) ?? [];
            var now = after.Tables.GetValueOrDefault(name) ?? [];
            if (!before.SequenceEqual(now, StringComparer.Ordinal))
            {
                differences.Add($"{name}: {before.Count} row(s) before, {now.Count} after, {now.Except(before, StringComparer.Ordinal).Count()} new or changed");
            }
        }

        return differences;
    }
}
