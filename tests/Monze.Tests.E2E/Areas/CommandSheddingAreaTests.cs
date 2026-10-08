using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Areas;

/// <summary>
/// Regression for DEF-03 (capacity BP-5): the SDK starts a handler for every
/// command message without a bound, so a storm used to queue thousands of
/// replies behind the upstream budget and the bot stayed unresponsive long
/// after it. With Monze:Commands:MaxInFlight commands in flight, a further
/// command is dropped unanswered (and unclaimed, so a retry is served), and
/// the bot answers again as soon as the burst is over.
/// </summary>
public sealed class CommandSheddingAreaTests
{
    [DbFact]
    [Req("REQ-CMD-001")]
    public async Task A_command_over_the_in_flight_bound_is_shed_and_the_next_one_is_served()
    {
        await using var host = await E2EActions.StartAsync("command_shed", configuration: new Dictionary<string, string?>
        {
            ["Monze:Commands:MaxInFlight"] = "1"
        });
        var mark = await E2EOracles.MarkAsync(host);

        // The first command's reply is held for two seconds, keeping it in flight.
        host.Simulator.Faults.Delay(SimOperations.EphemeralMessageSend, TimeSpan.FromSeconds(2));
        var first = await host.Inbound.SayAsync(ClanId, GeneralId, MemberId, "*monze help");
        await E2EActions.WaitUntilAsync(
            host,
            async () => await host.ScalarAsync<long>("SELECT count(*) FROM command_inbox WHERE message_id = @id;", ("id", first.MessageId)) == 1,
            "the first command to be claimed");
        var shed = await host.Inbound.SayAsync(ClanId, GeneralId, Member2Id, "*monze help");
        await host.WaitForCommandStatusAsync(ClanId, GeneralId, first.MessageId, E2EOracles.Timeout);
        var served = await E2EActions.CommandAsync(host, GeneralId, Member2Id, "*monze help");

        // The first reply lands after the shed command was pushed, so replies
        // are matched by recipient instead of by input order.
        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { ResponsesUnchecked = true });
        var replies = host.Recorder.Since(mark.Sequence)
            .Where(static action => action.Kind is SimActionKind.SendMessage or SimActionKind.SendEphemeral or SimActionKind.UpdateEphemeral
                or SimActionKind.DeleteEphemeral or SimActionKind.UpdateMessage or SimActionKind.DeleteMessage)
            .ToList();
        Assert.All(replies, static action => Assert.Equal(SimActionKind.SendEphemeral, action.Kind));
        Assert.Single(replies, static action => action.TargetUserId == MemberId);
        Assert.Single(replies, static action => action.TargetUserId == Member2Id);
        Assert.Equal(2, replies.Count);
        Assert.Equal(0, await host.ScalarAsync<long>("SELECT count(*) FROM command_inbox WHERE message_id = @id;", ("id", shed.MessageId)));
        Assert.Equal(1, await host.ScalarAsync<long>("SELECT count(*) FROM command_inbox WHERE message_id = @id;", ("id", served.MessageId)));
    }
}
