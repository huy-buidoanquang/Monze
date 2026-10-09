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
/// while a command waits for its reply ack lets it finish within the stop's
/// drain, or records it as uncertain; either way it is answered at most once.
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
    /// WF-08: the stop waits (up to UncertainMarkTimeout) for a command whose
    /// reply is waiting for its delayed ack, so the reply reaches the platform
    /// once and the row is recorded as completed; every redelivery after the
    /// restart, inside the lease or after it, is a duplicate.
    /// </summary>
    [DbFact]
    [Req("REQ-INBOX-001", "REQ-HOST-011")]
    public async Task A_host_stopped_mid_command_finishes_it_and_answers_nothing_after_restart()
    {
        await using var first = await E2EActions.StartAsync("wf_inbox_stop");
        first.Simulator.Faults.Delay(SimOperations.ChannelMessageSend, TimeSpan.FromSeconds(1));
        var command = await first.Inbound.SayAsync(ClanId, GeneralId, MemberId, "*welcome on");
        await E2EActions.WaitUntilAsync(first, async () => await StatusAsync(first, command) == "processing", "the command to be claimed");
        await first.StopHostAsync();

        Assert.Equal("completed", await StatusAsync(first, command));
        Assert.Equal(1, Answers(first, command));
        await AssertRedeliveriesAreDuplicatesAsync(first, command, "wf_inbox_stop_2");
    }

    /// <summary>
    /// A reply whose ack has not come when the stop's drain ends (twice
    /// UncertainMarkTimeout): the client is closed under it, the command is
    /// recorded as uncertain and never run again, so the redeliveries after
    /// the restart are duplicates and it is answered at most once.
    /// </summary>
    [DbFact]
    [Req("REQ-INBOX-001", "REQ-HOST-011")]
    public async Task A_command_unacknowledged_when_the_stop_ends_is_uncertain_and_not_run_again()
    {
        await using var first = await E2EActions.StartAsync("wf_inbox_unacked");
        first.Simulator.Faults.Delay(SimOperations.ChannelMessageSend, TimeSpan.FromSeconds(30));
        var command = await first.Inbound.SayAsync(ClanId, GeneralId, MemberId, "*welcome on");
        await E2EActions.WaitUntilAsync(first, async () => await StatusAsync(first, command) == "processing", "the command to be claimed");
        await first.StopHostAsync();
        await E2EActions.WaitUntilAsync(first, async () => await StatusAsync(first, command) == "uncertain", "the command to be recorded as uncertain");
        foreach (var entry in first.Logs.Entries.Where(static entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Warning))
        {
            output.WriteLine($"  first host {entry.Level}: {entry.Message} {entry.Exception?.GetType().Name}");
        }

        await AssertRedeliveriesAreDuplicatesAsync(first, command, "wf_inbox_unacked_2");
    }

    private static async Task AssertRedeliveriesAreDuplicatesAsync(MonzeE2EHost first, SimPush command, string tag)
    {
        var answers = Answers(first, command);
        var mark = new E2EMark(first.Recorder.LastSequence, 0, null);
        await using var second = await E2EActions.StartAsync(tag, database: first.Database, simulator: first.Simulator, dataDirectory: first.DataDirectory);
        var inLease = await second.Inbound.RedeliverMessageAsync(command.MessageId);
        await second.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));

        // Time warp: a 'processing' claim's 60 s lease would have run out (none is left).
        await second.ScalarAsync<int>("UPDATE command_inbox SET locked_until = now() - interval '1 second' WHERE message_id = @message AND status = 'processing' RETURNING 1;", ("message", command.MessageId));
        var afterLease = await second.Inbound.RedeliverMessageAsync(command.MessageId);
        await second.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));

        await E2EOracles.AssertAsync(second, mark, ScenarioExpectation.Of((inLease, ResponseKind.Duplicate), (afterLease, ResponseKind.Duplicate)));
        Assert.Equal(answers, Answers(second, command));
        Assert.True(answers <= 1, "The command was answered twice.");
        await E2EOracles.AssertInvariantsAsync(second);
    }

    private static Task<string?> StatusAsync(MonzeE2EHost host, SimPush command)
        => host.ScalarAsync<string>("SELECT status FROM command_inbox WHERE message_id = @message;", ("message", command.MessageId));

    private static int Answers(MonzeE2EHost host, SimPush command)
        => host.World.MessagesIn(GeneralId).Count(message => message.SenderId == host.World.Bot.Id && message.ReplyToMessageId == command.MessageId);
}
