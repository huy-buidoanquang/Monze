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
/// the producers write them (kind Announcement, a unique dedupe key).
/// A dropped ack makes the row 'uncertain' and it is never resent, not even
/// by a restarted host. A server-side socket close while sends are in flight
/// loses messages for good (CAND-21, chaos run MZ-01).
/// </summary>
public sealed class OutboxWorkflowTests(ITestOutputHelper output)
{
    private const string AckWarning = "did not get an ack";
    private const string SocketWarning = "Mezon socket disconnected";

    [DbFact]
    [Req("REQ-OUT-001")]
    public async Task A_dropped_ack_leaves_the_row_uncertain_and_no_restart_resends_it()
    {
        var simulatorOptions = new MezonSimulatorOptions { SocketTimeoutMilliseconds = 1500 };
        await using var first = await E2EActions.StartAsync("wf_outbox_ack", simulatorOptions: simulatorOptions);
        var mark = await E2EOracles.MarkAsync(first);
        first.Simulator.Faults.DropResponse(SimOperations.ChannelMessageSend);
        await InsertAnnouncementsAsync(first, "ack", 1);
        await E2EActions.WaitUntilAsync(first, async () => await StatusAsync(first, "ack", 1) == "uncertain", "the row to become uncertain");
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await E2EOracles.AssertAsync(first, mark, new ScenarioExpectation { OtherOutputs = 1, AllowedWarnings = [AckWarning] });
        Assert.Equal(1, Delivered(first, "ack", 1));
        var row = Assert.Single(await first.RowsAsync("SELECT status, attempts, last_error, external_message_id FROM outbox_delivery WHERE dedupe_key = 'e2e-ack-0001';"));
        Assert.Equal(new object?[] { "uncertain", 1, "delivery-uncertain", null }, row);

        // Restart on the same database and platform: the uncertain row stays put.
        await first.StopHostAsync();
        await using var second = await E2EActions.StartAsync("wf_outbox_ack_restart", database: first.Database, simulator: first.Simulator, dataDirectory: first.DataDirectory);
        var restarted = await E2EOracles.MarkAsync(second);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await E2EOracles.AssertAsync(second, restarted, new ScenarioExpectation());
        Assert.Equal(1, Delivered(second, "ack", 1));
        Assert.Equal("uncertain", await StatusAsync(second, "ack", 1));
        await E2EOracles.AssertInvariantsAsync(second);
    }

    /// <summary>
    /// CAND-21 (chaos MZ-01: 80 of 6,000 rows undelivered 90 s after one
    /// server-side socket close). The SDK sends one message at a time; at the
    /// close one send is on the wire and the others are queued. Every one of
    /// them is lost for good:
    /// <list type="number">
    /// <item>The send on the wire never gets its ack. Mezon.Net.Sdk 1.6.2
    /// fails it with OperationCanceledException when the reconnect calls
    /// DisconnectAsync (MezonSocketClient.cs line 152), or it times out
    /// (TimeoutException) when the reconnect is quick. Monze's
    /// IsUncertainDeliveryFailure (Hosting/MonzeBot.Outbox.cs line 228) knows
    /// TaskCanceledException but not its base OperationCanceledException, so
    /// the first case counts as a definite failure; the second parks the row
    /// as 'uncertain' with no reconciliation.</item>
    /// <item>Queued sends meet the closed socket: the transport throws
    /// InvalidOperationException before anything leaves the process
    /// (MezonNetworkWebSocketTransporter.SendAsync, simulated identically).</item>
    /// <item>A definite failure reschedules the row as 'pending' with
    /// attempts = 1 (MonzeBot.Outbox.cs lines 176-189), and on the next claim
    /// OutboxPolicy.Decide (Monze.Domain/OutboxPolicy.cs lines 12-17) returns
    /// HoldForAdmin for any attempts &gt; 0, so the row becomes 'uncertain'
    /// ("retry-limit", MonzeBot.Outbox.cs line 128) without a second send.</item>
    /// </list>
    /// A restart does not help: uncertain rows are never claimed again.
    /// </summary>
    [DbFact]
    [Req("REQ-OUT-001", "REQ-CONN-001")]
    public async Task A_socket_close_with_sends_in_flight_loses_messages_CAND_21()
    {
        var simulatorOptions = new MezonSimulatorOptions { SocketTimeoutMilliseconds = 2000 };
        await using var host = await E2EActions.StartAsync("wf_outbox_close", simulatorOptions: simulatorOptions);
        var mark = await E2EOracles.MarkAsync(host);
        host.Simulator.Faults.Delay(SimOperations.ChannelMessageSend, TimeSpan.FromMilliseconds(500), times: 40);
        await InsertAnnouncementsAsync(host, "close", 40);
        await E2EActions.WaitUntilAsync(host, async () => await host.ScalarAsync<long>("SELECT count(*) FROM outbox_delivery WHERE status = 'sending';") >= 32, "sends in flight");

        // The SDK sends one message at a time: the first claimed send is on
        // the wire waiting for its delayed ack when the server closes the
        // socket; two refused handshakes keep the socket down for a while so
        // the queued sends meet a closed socket.
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        host.Simulator.Faults.RefuseConnect(times: 2);
        Assert.Equal(1, await host.Inbound.CloseSocketAsync());
        await InsertAnnouncementsAsync(host, "down", 10);
        await E2EActions.WaitUntilAsync(
            host,
            async () => await host.ScalarAsync<long>("SELECT count(*) FROM outbox_delivery WHERE status IN ('pending', 'sending');") == 0,
            "every outbox row to settle",
            TimeSpan.FromSeconds(45));

        var rows = await host.RowsAsync("SELECT dedupe_key, status, COALESCE(last_error, '') FROM outbox_delivery ORDER BY id;");
        var lost = rows.Where(row => Delivered(host, (string)row[0]!) == 0).ToList();
        var duplicated = rows.Where(row => Delivered(host, (string)row[0]!) > 1).ToList();
        output.WriteLine($"rows={rows.Count} sent={rows.Count(static row => (string)row[1]! == "sent")} lost={lost.Count} duplicated={duplicated.Count}");
        foreach (var group in rows.GroupBy(row => $"{row[1]}/{row[2]}/{(Delivered(host, (string)row[0]!) > 0 ? "delivered" : "lost")}"))
        {
            output.WriteLine($"  {group.Key}: {group.Count()}");
        }

        foreach (var group in host.Logs.Entries.Where(static entry => entry.Message.Contains(AckWarning, StringComparison.Ordinal)).GroupBy(static entry => entry.Exception?.GetType().Name ?? "none"))
        {
            output.WriteLine($"  send failure {group.Key}: {group.Count()}");
        }

        Assert.Empty(duplicated);
        Assert.All(lost, static row => Assert.Equal("uncertain", row[1]));
        await KnownDefect.ExpectFailureAsync("CAND-21", () =>
        {
            Assert.Empty(lost);
            return Task.CompletedTask;
        });

        var sends = host.Recorder.Since(mark.Sequence).Count(static action => action.Kind == SimActionKind.SendMessage && action.ResponseCode == 0);
        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { OtherOutputs = sends, AllowedWarnings = [AckWarning, SocketWarning] });
        await E2EOracles.AssertInvariantsAsync(host);
    }

    /// <summary>
    /// WF-02 (new, found by the meeting random walks): a row that depends on
    /// another (a summary's action items depend on the summary) is claimed
    /// only once its parent is 'sent' (PostgresOutboxRepository.Delivery.cs
    /// ClaimDueOutboxAsync, depends_on_id condition). When the parent ends
    /// 'uncertain' (a lost ack, or CAND-21) the child stays 'pending'
    /// forever: it is never sent, never held, and nothing reports it.
    /// Correct behaviour asserted: the child leaves 'pending' (sent, or held
    /// for an operator) within 5 s.
    /// </summary>
    [DbFact]
    [Req("REQ-OUT-001")]
    public async Task A_child_of_an_uncertain_row_stays_pending_forever_WF_02()
    {
        var simulatorOptions = new MezonSimulatorOptions { SocketTimeoutMilliseconds = 1500 };
        await using var host = await E2EActions.StartAsync("wf_outbox_child", simulatorOptions: simulatorOptions);
        var mark = await E2EOracles.MarkAsync(host);
        host.Simulator.Faults.DropResponse(SimOperations.ChannelMessageSend);
        await InsertAnnouncementsAsync(host, "parent", 1);
        await host.ScalarAsync<int>("""
            INSERT INTO outbox_delivery(clan_id, channel_id, kind, dedupe_key, body, depends_on_id)
            SELECT clan_id, channel_id, 'Announcement', 'e2e-child-0001', 'Notice e2e-child-0001.', id
            FROM outbox_delivery WHERE dedupe_key = 'e2e-parent-0001'
            RETURNING 1;
            """);
        await E2EActions.WaitUntilAsync(host, async () => await StatusAsync(host, "parent", 1) == "uncertain", "the parent to become uncertain");
        await KnownDefect.ExpectFailureAsync("WF-02", () => E2EActions.WaitUntilAsync(
            host,
            async () => await StatusAsync(host, "child", 1) != "pending",
            "the child to leave pending",
            TimeSpan.FromSeconds(5)));
        Assert.Equal("pending", await StatusAsync(host, "child", 1));
        Assert.Equal(0, Delivered(host, "child", 1));
        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { OtherOutputs = 1, AllowedWarnings = [AckWarning] });
        await E2EOracles.AssertInvariantsAsync(host);
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
