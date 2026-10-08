using Monze.Testing;
using Monze.Testing.Postgres;
using Monze.Tests.Migrations;
using Npgsql;
using Xunit;

namespace Monze.Tests.Repositories;

/// <summary>
/// A throwaway database cloned from a migrated template that also records
/// every status change of the state-machine tables (meeting_session,
/// outbox_delivery, meeting_schedule, command_inbox, interaction_inbox) in
/// test_status_audit. <see cref="AssertTransitionsAsync"/> fails on any
/// transition outside <see cref="Allowed"/>.
/// </summary>
internal sealed class AuditedDatabase : IAsyncDisposable
{
    public const string TemplateName = "monze_t_template_audit";

    // Every status change the repositories are allowed to make, as
    // "table:old>new" (old is "null" for an insert).
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "meeting_session:null>requested",
        "meeting_session:requested>suggested",
        "meeting_session:requested>cancelled",
        "meeting_session:requested>expired",
        "meeting_session:suggested>live",
        "meeting_session:suggested>expired",
        "meeting_session:suggested>cancelled",
        "meeting_session:live>summary_pending",
        "meeting_session:live>posted",
        "meeting_session:summary_pending>posted",
        "meeting_session:summary_pending>summary_failed",
        "meeting_session:summary_failed>posted",
        "outbox_delivery:null>pending",
        "outbox_delivery:pending>sending",
        "outbox_delivery:sending>sent",
        "outbox_delivery:sending>pending",
        "outbox_delivery:sending>uncertain",
        "meeting_schedule:null>active",
        "meeting_schedule:active>running",
        "meeting_schedule:active>cancelled",
        "meeting_schedule:running>active",
        "meeting_schedule:running>completed",
        "meeting_schedule:running>cancelled",
        "command_inbox:null>processing",
        "command_inbox:processing>completed",
        "command_inbox:processing>uncertain",
        "interaction_inbox:null>processing",
        "interaction_inbox:processing>completed",
        "interaction_inbox:processing>uncertain"
    };

    private const string AuditSql = """
        CREATE TABLE test_status_audit (
          table_name TEXT NOT NULL,
          old_status TEXT NULL,
          new_status TEXT NOT NULL,
          changed_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp()
        );

        CREATE FUNCTION test_record_status() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          -- Rows the test seeds directly are not repository transitions.
          IF current_setting('monze.test_seed', true) = 'on' THEN
            RETURN NEW;
          END IF;
          IF TG_OP = 'INSERT' THEN
            INSERT INTO test_status_audit(table_name, old_status, new_status) VALUES (TG_TABLE_NAME, NULL, NEW.status);
          ELSIF NEW.status IS DISTINCT FROM OLD.status THEN
            INSERT INTO test_status_audit(table_name, old_status, new_status) VALUES (TG_TABLE_NAME, OLD.status, NEW.status);
          END IF;
          RETURN NEW;
        END
        $$;

        CREATE TRIGGER test_status_audit AFTER INSERT OR UPDATE OF status ON meeting_session FOR EACH ROW EXECUTE FUNCTION test_record_status();
        CREATE TRIGGER test_status_audit AFTER INSERT OR UPDATE OF status ON outbox_delivery FOR EACH ROW EXECUTE FUNCTION test_record_status();
        CREATE TRIGGER test_status_audit AFTER INSERT OR UPDATE OF status ON meeting_schedule FOR EACH ROW EXECUTE FUNCTION test_record_status();
        CREATE TRIGGER test_status_audit AFTER INSERT OR UPDATE OF status ON command_inbox FOR EACH ROW EXECUTE FUNCTION test_record_status();
        CREATE TRIGGER test_status_audit AFTER INSERT OR UPDATE OF status ON interaction_inbox FOR EACH ROW EXECUTE FUNCTION test_record_status();
        """;

    private static readonly SemaphoreSlim TemplateGate = new(1, 1);
    private static readonly HashSet<string> TemplateServers = new(StringComparer.Ordinal);

    private readonly CampaignDatabase _database;

    private AuditedDatabase(CampaignDatabase database, NpgsqlDataSource dataSource)
    {
        _database = database;
        DataSource = dataSource;
    }

    public string ConnectionString => _database.ConnectionString;

    public NpgsqlDataSource DataSource { get; }

    public static async Task<AuditedDatabase> CreateAsync(string tag)
    {
        var server = TestPostgres.ConnectionString;
        await EnsureTemplateAsync(server);
        var database = await CampaignDatabase.CloneAsync(server, TemplateName, tag);
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { MaxPoolSize = 64 };
        return new AuditedDatabase(database, NpgsqlDataSource.Create(builder.ConnectionString));
    }

    public async Task<IReadOnlyList<string>> TransitionsAsync()
        => await MigrationSteps.RowsAsync(
            ConnectionString,
            "SELECT DISTINCT table_name || ':' || COALESCE(old_status, 'null') || '>' || new_status FROM test_status_audit ORDER BY 1;");

    public async Task AssertTransitionsAsync()
    {
        var unexpected = (await TransitionsAsync()).Where(static transition => !Allowed.Contains(transition)).ToList();
        Assert.True(unexpected.Count == 0, $"Unexpected status transitions: {string.Join(", ", unexpected)}");
    }

    /// <summary>Seeds data directly; the audit trigger ignores these statements.</summary>
    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var seed = new NpgsqlCommand("SET LOCAL monze.test_seed = 'on';", connection, transaction))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _database.DisposeAsync();
    }

    private static async Task EnsureTemplateAsync(string server)
    {
        await TemplateGate.WaitAsync();
        try
        {
            if (TemplateServers.Contains(server))
            {
                return;
            }

            await MigrationTemplate.EnsureAsync(server);
            await CampaignDatabase.CreateNamedFromTemplateAsync(server, MigrationTemplate.Name, TemplateName);
            var connectionString = new NpgsqlConnectionStringBuilder(server) { Database = TemplateName, Pooling = false }.ConnectionString;
            await MigrationSteps.ExecuteAsync(connectionString, AuditSql);
            NpgsqlConnection.ClearAllPools();
            TemplateServers.Add(server);
        }
        finally
        {
            TemplateGate.Release();
        }
    }
}
