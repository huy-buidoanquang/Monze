using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Monze.Testing.Postgres;

/// <summary>
/// A canonical description of the public schema: tables, columns (type,
/// nullability, default), constraints, indexes, sequences, views, triggers
/// and functions, one sorted line each. Two databases with equal lines have
/// the same schema; <see cref="Hash"/> condenses it for reports.
/// </summary>
public static class SchemaFingerprint
{
    private const string Query = """
        SELECT 'table ' || c.relname
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
        UNION ALL
        SELECT 'column ' || table_name || '.' || column_name || ' ' || data_type || ' null=' || is_nullable
               || ' default=' || COALESCE(column_default, '-')
        FROM information_schema.columns
        WHERE table_schema = 'public'
        UNION ALL
        SELECT 'constraint ' || con.conrelid::regclass::text || ' ' || con.conname || ' ' || pg_get_constraintdef(con.oid)
        FROM pg_constraint con
        WHERE con.connamespace = 'public'::regnamespace
        UNION ALL
        SELECT 'index ' || indexname || ' ' || indexdef
        FROM pg_indexes
        WHERE schemaname = 'public'
        UNION ALL
        SELECT 'sequence ' || sequence_name || ' ' || data_type
        FROM information_schema.sequences
        WHERE sequence_schema = 'public'
        UNION ALL
        SELECT 'view ' || table_name
        FROM information_schema.views
        WHERE table_schema = 'public'
        UNION ALL
        SELECT 'trigger ' || event_object_table || ' ' || trigger_name || ' ' || action_timing || ' ' || event_manipulation
        FROM information_schema.triggers
        WHERE trigger_schema = 'public'
        UNION ALL
        SELECT 'function ' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')'
        FROM pg_proc p
        WHERE p.pronamespace = 'public'::regnamespace
        ORDER BY 1;
        """;

    public static async Task<IReadOnlyList<string>> LinesAsync(string connectionString)
    {
        TestPostgres.AssertCampaignDatabase(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }

    public static string Hash(IEnumerable<string> lines)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))))[..16];

    /// <summary>Lines present in only one of the two fingerprints, prefixed with - or +.</summary>
    public static string Difference(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var left = expected.ToHashSet(StringComparer.Ordinal);
        var right = actual.ToHashSet(StringComparer.Ordinal);
        return string.Join(
            Environment.NewLine,
            left.Except(right).Select(static line => "- " + line).Concat(right.Except(left).Select(static line => "+ " + line)).Take(20));
    }
}
