using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Workflows;

/// <summary>
/// The outbox (Hosting/MonzeBot.Outbox.cs, PostgresOutboxRepository) under
/// transport faults. Rows are written straight into the campaign database as
/// the producers write them (kind Announcement, a unique dedupe key). A
/// delivery that may have reached the channel is never resent blindly: it is
/// reconciled against the channel history (found: recorded as sent; absent:
/// resent). Tests move the 30-second reconciliation gate (due_at) forward on
/// the campaign database instead of waiting for it.
/// </summary>
public sealed class OutboxWorkflowTests(ITestOutputHelper output)
{
    private const string AckWarning = "did not get an ack";
    private const string SocketWarning = "Mezon socket disconnected";
    private const string ReconcileWarning = "reconciliation could not read the channel";

    [DbFact]
    [Req("REQ-OUT-001")]
    public async Task A_dropped_ack_is_reconciled_as_sent_and_never_resent()
    {
        var simulatorOptions = new MezonSimulatorOptions { SocketTimeoutMilliseconds = 1500 };
        await using var first = await E2EActions.StartAsync("wf_outbox_ack", simulatorOptions: simulatorOptions);
        var mark = await E2EOracles.MarkAsync(first);
        first.Simulator.Faults.DropResponse(SimOperations.ChannelMessageSend);
        await InsertAnnouncementsAsync(first, "ack", 1);
        await E2EActions.WaitUntilAsync(first, async () => await StatusAsync(first, "ack", 1) == "uncertain", "the row to become uncertain");
        await WarpAsync(first);
        await E2EActions.WaitUntilAsync(first, async () => await StatusAsync(first, "ack", 1) == "sent", "the row to be reconciled as sent");
        await E2EOracles.AssertAsync(first, mark, new ScenarioExpectation { OtherOutputs = 1, AllowedWarnings = [AckWarning] });
        Assert.Equal(1, Delivered(first, "ack", 1));
        var row = Assert.Single(await first.RowsAsync("SELECT status, external_message_id IS NOT NULL FROM outbox_delivery WHERE dedupe_key = 'e2e-ack-0001';"));
        Assert.Equal(new object?[] { "sent", true }, row);

        // Restart on the same database and platform: nothing is sent again.
        await first.StopHostAsync();
        await using var second = await E2EActions.StartAsync("wf_outbox_ack_restart", database: first.Database, simulator: first.Simulator, dataDirectory: first.DataDirectory);
        var restarted = await E2EOracles.MarkAsync(second);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await E2EOracles.AssertAsync(second, restarted, new ScenarioExpectation());
        Assert.Equal(1, Delivered(second, "ack", 1));
        await E2EOracles.AssertInvariantsAsync(second);
    }

    /// <summary>
    /// Regression for DEF-07 (chaos PG-06): the ack arrived but the database
    /// was lost before the completion was written, so the row stayed
    /// 'sending' until its lease expired. It is found on the channel and
    /// recorded with the delivered message's id instead of being resent.
    /// </summary>
    [DbFact]
    [Req("REQ-OUT-001")]
    public async Task A_send_whose_completion_was_lost_is_found_after_its_lease_expires()
    {
        await using var host = await E2EActions.StartAsync("wf_outbox_lost_completion");
        var mark = await E2EOracles.MarkAsync(host);
        await InsertAnnouncementsAsync(host, "lost", 1);
        await E2EActions.WaitUntilAsync(host, async () => await StatusAsync(host, "lost", 1) == "sent", "the row to be sent");
        var delivered = await host.ScalarAsync<long>("SELECT external_message_id FROM outbox_delivery WHERE dedupe_key = 'e2e-lost-0001';");

        // What a lost completion leaves behind: the send's lease, expired.
        await host.ScalarAsync<int>("UPDATE outbox_delivery SET status = 'sending', external_message_id = NULL, lease_token = 'lost', locked_until = now() - interval '1 second' WHERE dedupe_key = 'e2e-lost-0001' RETURNING 1;");
        await E2EActions.WaitUntilAsync(host, async () => await StatusAsync(host, "lost", 1) == "sent", "the row to be reconciled as sent");

        Assert.Equal(delivered, await host.ScalarAsync<long>("SELECT external_message_id FROM outbox_delivery WHERE dedupe_key = 'e2e-lost-0001';"));
        Assert.Equal(1, Delivered(host, "lost", 1));
        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { OtherOutputs = 1 });
        await E2EOracles.AssertInvariantsAsync(host);
    }

    /// <summary>
    /// Regression for CAND-21 (chaos MZ-01 lost about 100 rows per socket
    /// close). At a server-side close one send is on the wire and the others
    /// are queued. Queued sends meet the closed socket (the transport throws
    /// InvalidOperationException before anything leaves the process) and are
    /// retried; the send on the wire fails with OperationCanceledException or
    /// a timeout and is reconciled against the channel. Every message is
    /// delivered exactly once.
    /// </summary>
    [DbFact]
    [Req("REQ-OUT-001", "REQ-CONN-001")]
    public async Task A_socket_close_with_sends_in_flight_delivers_every_message_once()
    {
        var simulatorOptions = new MezonSimulatorOptions { SocketTimeoutMilliseconds = 2000 };
        await using var host = await E2EActions.StartAsync("wf_outbox_close", simulatorOptions: simulatorOptions);
        var mark = await E2EOracles.MarkAsync(host);
        host.Simulator.Faults.Delay(SimOperations.ChannelMessageSend, TimeSpan.FromMilliseconds(500), times: 40);
        await InsertAnnouncementsAsync(host, "close", 40);
        await E2EActions.WaitUntilAsync(host, async () => await host.ScalarAsync<long>("SELECT count(*) FROM outbox_delivery WHERE status = 'sending';") >= 32, "sends in flight");

        // The first claimed send is on the wire waiting for its delayed ack
        // when the server closes the socket; two refused handshakes keep the
        // socket down for a while so the queued sends meet a closed socket.
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        host.Simulator.Faults.RefuseConnect(times: 2);
        Assert.Equal(1, await host.Inbound.CloseSocketAsync());
        await InsertAnnouncementsAsync(host, "down", 10);
        await E2EActions.WaitUntilAsync(
            host,
            async () =>
            {
                await WarpAsync(host);
                return await host.ScalarAsync<long>("SELECT count(*) FROM outbox_delivery WHERE status <> 'sent';") == 0;
            },
            "every outbox row to be sent",
            TimeSpan.FromSeconds(90));

        var rows = await host.RowsAsync("SELECT dedupe_key, status, COALESCE(last_error, '') FROM outbox_delivery ORDER BY id;");
        var lost = rows.Where(row => Delivered(host, (string)row[0]!) == 0).ToList();
        var duplicated = rows.Where(row => Delivered(host, (string)row[0]!) > 1).ToList();
        output.WriteLine($"rows={rows.Count} lost={lost.Count} duplicated={duplicated.Count}");
        Assert.Equal(50, rows.Count);
        Assert.Empty(lost);
        Assert.Empty(duplicated);

        var sends = host.Recorder.Since(mark.Sequence).Count(static action => action.Kind == SimActionKind.SendMessage && action.ResponseCode == 0);
        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { OtherOutputs = sends, AllowedWarnings = [AckWarning, SocketWarning, ReconcileWarning] });
        await E2EOracles.AssertInvariantsAsync(host);
    }

    /// <summary>
    /// Regression for WF-02: a row that depends on another (a summary's action
    /// items depend on the summary) used to stay 'pending' forever when its
    /// parent ended 'uncertain'. Now the parent is reconciled (here: found on
    /// the channel) and the child is sent after it; a child whose parent is
    /// held for an administrator is held too, with a reason.
    /// </summary>
    [DbFact]
    [Req("REQ-OUT-001")]
    public async Task A_child_follows_its_reconciled_parent_and_is_held_with_a_held_parent()
    {
        var simulatorOptions = new MezonSimulatorOptions { SocketTimeoutMilliseconds = 1500 };
        await using var host = await E2EActions.StartAsync("wf_outbox_child", simulatorOptions: simulatorOptions);
        var mark = await E2EOracles.MarkAsync(host);
        host.Simulator.Faults.DropResponse(SimOperations.ChannelMessageSend);
        await InsertAnnouncementsAsync(host, "parent", 1);
        await InsertChildAsync(host, "child", "parent");
        await E2EActions.WaitUntilAsync(host, async () => await StatusAsync(host, "parent", 1) == "uncertain", "the parent to become uncertain");
        Assert.Equal("pending", await StatusAsync(host, "child", 1));
        await WarpAsync(host);
        await E2EActions.WaitUntilAsync(host, async () => await StatusAsync(host, "child", 1) == "sent", "the child to be sent after its parent", TimeSpan.FromSeconds(15));
        Assert.Equal("sent", await StatusAsync(host, "parent", 1));
        Assert.Equal(1, Delivered(host, "parent", 1));
        Assert.Equal(1, Delivered(host, "child", 1));

        // A parent held for an administrator holds its child.
        await host.ScalarAsync<int>(
            "INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, status, last_error) VALUES (@clan, @channel, 'Announcement', 'e2e-held-0001', 'Notice e2e-held-0001.', 'uncertain', 'delivery-rejected') RETURNING 1;",
            ("clan", ClanId),
            ("channel", GeneralId));
        await InsertChildAsync(host, "orphan", "held");
        await E2EActions.WaitUntilAsync(host, async () => await StatusAsync(host, "orphan", 1) == "uncertain", "the child of a held parent to be held", TimeSpan.FromSeconds(15));
        Assert.Equal("dependency-held", await host.ScalarAsync<string>("SELECT last_error FROM outbox_delivery WHERE dedupe_key = 'e2e-orphan-0001';"));
        Assert.Equal(0, Delivered(host, "orphan", 1));
        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { OtherOutputs = 2, AllowedWarnings = [AckWarning] });
        await E2EOracles.AssertInvariantsAsync(host);
    }

    private static Task<int> InsertChildAsync(MonzeE2EHost host, string child, string parent)
        => host.ScalarAsync<int>(
            "INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, depends_on_id) SELECT clan_id, channel_id, 'Announcement', @key, @body, id FROM outbox_delivery WHERE dedupe_key = @parent RETURNING 1;",
            ("key", $"e2e-{child}-0001"),
            ("body", $"Notice e2e-{child}-0001."),
            ("parent", $"e2e-{parent}-0001"));

    /// <summary>
    /// Moves the 30 s reconciliation gate and retry backoffs to now (campaign
    /// database only), so a test observes minutes of outbox recovery in seconds.
    /// </summary>
    private static async Task WarpAsync(MonzeE2EHost host)
    {
        await using var connection = new NpgsqlConnection(host.Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE outbox_delivery SET due_at = now() WHERE ((status = 'uncertain' AND last_error = 'delivery-uncertain') OR status = 'pending') AND due_at > now();",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Inserts <paramref name="count"/> Announcement rows for clan A's general channel.</summary>
    internal static async Task InsertAnnouncementsAsync(MonzeE2EHost host, string prefix, int count)
    {
        await using var connection = new NpgsqlConnection(host.Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body)
            SELECT @clan, @channel, 'Announcement', 'e2e-' || @prefix || '-' || lpad(n::text, 4, '0'), 'Notice e2e-' || @prefix || '-' || lpad(n::text, 4, '0') || '.'
            FROM generate_series(1, @count) AS n;
            """, connection);
        command.Parameters.AddWithValue("clan", ClanId);
        command.Parameters.AddWithValue("channel", GeneralId);
        command.Parameters.AddWithValue("prefix", prefix);
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync();
    }

    internal static Task<string?> StatusAsync(MonzeE2EHost host, string prefix, int number)
        => host.ScalarAsync<string>("SELECT status FROM outbox_delivery WHERE dedupe_key = @key;", ("key", $"e2e-{prefix}-{number:0000}"));

    /// <summary>How many messages with the row's body the platform stored in clan A's general channel.</summary>
    internal static int Delivered(MonzeE2EHost host, string prefix, int number)
        => Delivered(host, $"e2e-{prefix}-{number:0000}");

    private static int Delivered(MonzeE2EHost host, string dedupeKey)
        => host.World.MessagesIn(GeneralId).Count(message => message.SenderId == host.World.Bot.Id && message.ContentJson.Contains($"Notice {dedupeKey}.", StringComparison.Ordinal));
}
