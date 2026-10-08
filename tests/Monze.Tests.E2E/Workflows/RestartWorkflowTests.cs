using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using Xunit.Abstractions;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Workflows;

/// <summary>
/// Restarts on the same database, platform and data directory. A new host
/// rejoins every clan on a new socket, delivers outbox rows written while it
/// was down exactly once, answers new commands and repeats nothing (a
/// gateway redelivery of an answered command is a duplicate). A host stopped
/// while a command waits for its reply ack never delivers that reply; the
/// command_inbox row it leaves decides whether a later redelivery is answered.
/// </summary>
public sealed class RestartWorkflowTests(ITestOutputHelper output)
{
    [DbFact]
    [Req("REQ-HOST-011", "REQ-CONN-001", "REQ-OUT-001", "REQ-INBOX-001")]
    public async Task A_restart_rejoins_resumes_the_outbox_and_repeats_nothing()
    {
        await using var first = await E2EActions.StartAsync("wf_restart");
        var before = await E2EOracles.MarkAsync(first);
        var refused = await E2EActions.CommandAsync(first, GeneralId, MemberId, "*welcome on");
        var on = await E2EActions.CommandAsync(first, GeneralId, OwnerId, "*welcome on");
        await E2EOracles.AssertAsync(first, before, ScenarioExpectation.Of((refused, ResponseKind.Reply), (on, ResponseKind.Ephemeral)));
        var firstSession = Assert.Single(first.OwnSessions);

        await first.StopHostAsync();
        Assert.False(firstSession.IsConnected);
        await OutboxWorkflowTests.InsertAnnouncementsAsync(first, "restart", 2);
        var mark = new E2EMark(first.Recorder.LastSequence, 0, null);
        await using var second = await E2EActions.StartAsync("wf_restart_2", database: first.Database, simulator: first.Simulator, dataDirectory: first.DataDirectory);
        var secondSession = Assert.Single(second.OwnSessions);
        Assert.NotSame(firstSession, secondSession);
        Assert.Equal(new[] { ClanId, OtherClanId }, secondSession.JoinedClans);
        await E2EActions.WaitUntilAsync(second, async () => await OutboxWorkflowTests.StatusAsync(second, "restart", 2) == "sent", "the outbox rows written while down");

        var redelivered = await second.Inbound.RedeliverMessageAsync(refused.MessageId);
        var again = await E2EActions.CommandAsync(second, GeneralId, MemberId, "*welcome on");
        await E2EOracles.AssertAsync(second, mark, new ScenarioExpectation
        {
            Inputs = ScenarioExpectation.Of((redelivered, ResponseKind.Duplicate), (again, ResponseKind.Reply)).Inputs,
            OtherOutputs = 2
        });
        Assert.Equal(1, OutboxWorkflowTests.Delivered(second, "restart", 1));
        Assert.Equal(1, OutboxWorkflowTests.Delivered(second, "restart", 2));
        Assert.Contains(MonzeMessages.WelcomeAdminOnly, E2EContent.Visible(E2EActions.NewMessageAfter(second, again, GeneralId)));
        Assert.Equal(1, second.Recorder.Actions.Count(action => action.Kind == SimActionKind.SendMessage && action.ReplyToMessageId == refused.MessageId));
        await E2EOracles.AssertInvariantsAsync(second);
    }

    /// <summary>
    /// The uncertain path of the command inbox (Hosting/MonzeBot.CommandInbox.cs):
    /// the reply is waiting for its (delayed) ack when the host stops, and it
    /// never reaches the platform. The SDK fails the pending ack with
    /// OperationCanceledException; HandleMonzeCoreAsync does not recognise it
    /// as cancellation (the command context token is not cancelled), logs
    /// "Monze command failed" at Error and tries a TemporaryFailure reply on
    /// the closed socket (Hosting/MonzeBot.Commands.cs lines 148-165).
    /// MarkCommandUncertainAsync links its timeout to the runtime token, which
    /// shutdown has already cancelled, so the row is not marked 'uncertain'
    /// and stays 'processing' under its 60 s lease. A redelivery inside the
    /// lease is a duplicate; once the lease expired a redelivery is claimed
    /// again and answered, once.
    /// </summary>
    [DbFact]
    [Req("REQ-INBOX-001", "REQ-HOST-011")]
    public async Task A_host_stopped_mid_command_answers_at_most_once_after_restart()
    {
        await using var first = await E2EActions.StartAsync("wf_inbox_stop");
        first.Simulator.Faults.Delay(SimOperations.ChannelMessageSend, TimeSpan.FromSeconds(3));
        var command = await first.Inbound.SayAsync(ClanId, GeneralId, MemberId, "*welcome on");
        await E2EActions.WaitUntilAsync(
            first,
            async () => await first.ScalarAsync<string>("SELECT status FROM command_inbox WHERE message_id = @message;", ("message", command.MessageId)) == "processing",
            "the command to be claimed");
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await first.StopHostAsync();
        var status = await first.ScalarAsync<string>("SELECT status FROM command_inbox WHERE message_id = @message;", ("message", command.MessageId));
        output.WriteLine($"command_inbox after a stop mid-command: {status}");
        foreach (var entry in first.Logs.Entries.Where(static entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Warning))
        {
            output.WriteLine($"  first host {entry.Level}: {entry.Message} {entry.Exception?.GetType().Name}");
        }

        Assert.Equal(0, Answers(first, command));

        var mark = new E2EMark(first.Recorder.LastSequence, 0, null);
        await using var second = await E2EActions.StartAsync("wf_inbox_stop_2", database: first.Database, simulator: first.Simulator, dataDirectory: first.DataDirectory);
        var inLease = await second.Inbound.RedeliverMessageAsync(command.MessageId);
        await second.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        Assert.Equal(0, Answers(second, command));

        // Time warp: the claim's 60 s lease ran out.
        await second.ScalarAsync<int>("UPDATE command_inbox SET locked_until = now() - interval '1 second' WHERE message_id = @message AND status = 'processing' RETURNING 1;", ("message", command.MessageId));
        var afterLease = await second.Inbound.RedeliverMessageAsync(command.MessageId);
        if (status == "processing")
        {
            await second.WaitForCommandStatusAsync(ClanId, GeneralId, command.MessageId, E2EOracles.Timeout);
        }

        await second.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        var finalStatus = await second.ScalarAsync<string>("SELECT status FROM command_inbox WHERE message_id = @message;", ("message", command.MessageId));
        output.WriteLine($"command_inbox after the redeliveries: {finalStatus}, answers {Answers(second, command)}");

        Assert.True(Answers(second, command) <= 1, "The command was answered twice.");
        var expected = status == "processing"
            ? ScenarioExpectation.Of((inLease, ResponseKind.Duplicate), (afterLease, ResponseKind.Reply))
            : ScenarioExpectation.Of((inLease, ResponseKind.Duplicate), (afterLease, ResponseKind.Duplicate));
        await E2EOracles.AssertAsync(second, mark, expected);
        Assert.Equal("processing", status);
        Assert.Equal(1, Answers(second, command));
        await E2EOracles.AssertInvariantsAsync(second);
    }

    private static int Answers(MonzeE2EHost host, SimPush command)
        => host.World.MessagesIn(GeneralId).Count(message => message.SenderId == host.World.Bot.Id && message.ReplyToMessageId == command.MessageId);
}
