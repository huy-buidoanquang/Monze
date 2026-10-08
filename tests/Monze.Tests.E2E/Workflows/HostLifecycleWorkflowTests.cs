using System.Net;
using Microsoft.Extensions.Logging;
using Monze.Hosting;
using Monze.Infrastructure.Persistence;
using Monze.Simulator;
using Monze.Testing;
using Monze.Testing.Harness;
using Monze.Testing.Postgres;
using Monze.Tests.E2E.Harness;
using Npgsql;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Workflows;

/// <summary>
/// REQ-HOST-011, the bot lifecycle: StartupSchemaValidator gates start-up
/// (hosted services start in order and MonzeBot and the maintenance worker
/// await StartupReadiness), so nothing logs in while the schema check is
/// blocked and a database without the schema never gets a login; once the
/// check passes the bot logs in and the workers run; a graceful stop closes
/// the socket from the client, leaves no lease behind, flushes the SQLite
/// message store and logs no warning except the known disconnect warning.
/// </summary>
public sealed class HostLifecycleWorkflowTests
{
    private const string DisconnectWarning = "Mezon socket disconnected; live voice occupancy was invalidated.";

    [DbFact]
    [Req("REQ-HOST-011")]
    public async Task Workers_wait_for_the_schema_check_and_a_graceful_stop_drains()
    {
        await using var database = await CampaignDatabase.CreateEmptyAsync(TestPostgres.ConnectionString, "e2e_wf_lifecycle");
        await using (var dataSource = NpgsqlDataSource.Create(database.ConnectionString))
        {
            await PostgresMigrator.ApplyAsync(dataSource, database.ConnectionString, CancellationToken.None);
        }

        await using var simulator = new MezonSimulator(AreaWorld.Create());

        // Hold the schema check: an exclusive lock on schema_migrations.
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        var transaction = await blocker.BeginTransactionAsync();
        await using (var lockTable = new NpgsqlCommand("LOCK TABLE schema_migrations IN ACCESS EXCLUSIVE MODE;", blocker, transaction))
        {
            await lockTable.ExecuteNonQueryAsync();
        }

        var starting = E2EActions.StartAsync("wf_lifecycle", database: database, simulator: simulator);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.False(starting.IsCompleted);
        Assert.DoesNotContain(simulator.Recorder.Actions, static action => action.Kind is SimActionKind.Authenticate or SimActionKind.Connect);
        await transaction.RollbackAsync();
        await transaction.DisposeAsync();
        await using var host = await starting;
        Assert.Contains(simulator.Recorder.Actions, static action => action.Kind == SimActionKind.Authenticate);

        // Workers run: a command is answered and an outbox row is delivered.
        var mark = await E2EOracles.MarkAsync(host);
        var help = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*monze help");
        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of((help, ResponseKind.Ephemeral)));
        var outbox = await E2EOracles.MarkAsync(host);
        await OutboxWorkflowTests.InsertAnnouncementsAsync(host, "lifecycle", 1);
        await E2EActions.WaitUntilAsync(host, async () => await OutboxWorkflowTests.StatusAsync(host, "lifecycle", 1) == "sent", "the outbox row");
        await E2EOracles.AssertAsync(host, outbox, new ScenarioExpectation { OtherOutputs = 1 });
        host.AssertClean();

        // Graceful stop.
        var logsBeforeStop = host.Logs.Entries.Count;
        var session = Assert.Single(host.OwnSessions);
        await host.StopHostAsync();
        Assert.False(session.IsConnected);
        Assert.Contains(simulator.Recorder.Actions, action => action.Kind == SimActionKind.Disconnect && action.SessionId == session.SessionId);
        Assert.Equal(0L, await host.ScalarAsync<long>("""
            SELECT (SELECT count(*) FROM outbox_delivery WHERE status = 'sending')
                 + (SELECT count(*) FROM command_inbox WHERE status = 'processing')
                 + (SELECT count(*) FROM interaction_inbox WHERE status = 'processing')
                 + (SELECT count(*) FROM meeting_schedule WHERE status = 'running')
                 + (SELECT count(*) FROM meeting_session WHERE summary_lease_token IS NOT NULL);
            """));
        var sqlite = new DirectoryInfo(Path.Combine(host.DataDirectory.FullName, "data")).GetFiles("*", SearchOption.AllDirectories);
        Assert.Contains(sqlite, static file => file.Extension == ".db" && file.Length > 0);
        Assert.DoesNotContain(sqlite, static file => file.Name.EndsWith("-wal", StringComparison.Ordinal) && file.Length > 0);
        var afterStop = host.Logs.Entries.Skip(logsBeforeStop).Where(static entry => entry.Level >= LogLevel.Warning).ToList();
        Assert.All(afterStop, static entry => Assert.Equal(DisconnectWarning, entry.Message));
        await E2EOracles.AssertInvariantsAsync(host, leaseGrace: TimeSpan.Zero);
    }

    [DbFact]
    [Req("REQ-HOST-011")]
    public async Task A_database_without_the_schema_never_gets_a_login()
    {
        await using var database = await CampaignDatabase.CreateEmptyAsync(TestPostgres.ConnectionString, "e2e_wf_noschema");
        await using var simulator = new MezonSimulator(AreaWorld.Create());

        // A schema error is not retried like an unreachable database (CAND-29).
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => E2EActions.StartAsync("wf_noschema", database: database, simulator: simulator).WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.IsNotType<TimeoutException>(failure);
        Assert.DoesNotContain(simulator.Recorder.Actions, static action => action.Kind is SimActionKind.Authenticate or SimActionKind.Connect);
        Assert.Empty(simulator.Sessions);
    }

    /// <summary>
    /// Regression for CAND-29: PostgreSQL that refuses connections when the
    /// host starts (started together with the bot) is retried, and the schema
    /// check passes once it answers.
    /// </summary>
    [DbFact]
    [Req("REQ-HOST-011")]
    public async Task A_database_that_answers_late_passes_the_schema_check()
    {
        await using var database = await CampaignDatabase.CreateEmptyAsync(TestPostgres.ConnectionString, "e2e_wf_late");
        await using (var migrate = NpgsqlDataSource.Create(database.ConnectionString))
        {
            await PostgresMigrator.ApplyAsync(migrate, database.ConnectionString, CancellationToken.None);
        }

        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        await using var proxy = TcpFaultProxy.Start(new IPEndPoint(IPAddress.Loopback, connection.Port));
        proxy.Mode = TcpFaultMode.Refuse;
        connection.Port = proxy.Endpoint.Port;
        await using var dataSource = NpgsqlDataSource.Create(connection.ConnectionString);
        var logs = new HostLogSink();
        using var loggers = new LoggerFactory([logs]);
        var readiness = new StartupReadiness();
        var validator = new StartupSchemaValidator(dataSource, readiness, TimeProvider.System, loggers.CreateLogger<StartupSchemaValidator>());

        var start = validator.StartAsync(CancellationToken.None);
        await logs.WaitForAsync(static entry => entry.Level == LogLevel.Warning, TimeSpan.FromSeconds(10));
        Assert.False(readiness.Ready.IsCompleted);
        proxy.Mode = TcpFaultMode.Pass;

        await start.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(readiness.Ready.IsCompletedSuccessfully);
    }
}
