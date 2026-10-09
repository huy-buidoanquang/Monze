using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Monze.Ui;
using Xunit;
using Xunit.Abstractions;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Workflows;

/// <summary>
/// G32, the decision table of EnsurePrivateInteractionAsync
/// (Hosting/MonzeBot.InteractionResponses.cs lines 187-235) as the real host
/// applies it to the meeting-list Refresh button: only a binding by
/// (clan, channel, message) counts (the (clan, channel, user) fallback was
/// removed for CAND-19); it expires 30 minutes after it was last set; the
/// click is handled only when the binding's user is the clicking user.
/// Honest clicks come from the user's own client; forged clicks are
/// MessageButtonClicked events with a chosen user and message id, which
/// mezon-api forwards unchanged and the SDK marks server-authenticated.
/// Honest rows must match the correct outcome. The one forged row Monze
/// still accepts (R2: the owner's id forged on the owner's own card) can only
/// be closed by mezon-api setting user_id from the session; it stays CAND-19.
/// The cache clock is moved, not waited for.
/// </summary>
public sealed class PrivateInteractionDecisionTableTests(ITestOutputHelper output)
{
    [DbFact]
    [Req("REQ-INBOX-002")]
    [Covers("btn:monze_meeting_refresh")]
    public async Task Private_interaction_bindings_follow_the_decision_table()
    {
        var clock = new ManualCacheClock();
        await using var host = await E2EActions.StartAsync("wf_g32", services: clock.Install);
        var setup = await E2EOracles.MarkAsync(host);
        var memberList = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*meeting");
        var member2List = await E2EActions.CommandAsync(host, GeneralId, Member2Id, "*meeting");
        var refused = await E2EActions.CommandAsync(host, GeneralId, Member2Id, "*welcome on");
        await E2EOracles.AssertAsync(host, setup, ScenarioExpectation.Of(
            (memberList, ResponseKind.Ephemeral),
            (member2List, ResponseKind.Ephemeral),
            (refused, ResponseKind.Reply)));
        var card = E2EActions.NewMessageAfter(host, memberList, GeneralId).MessageId;
        var publicReply = E2EActions.NewMessageAfter(host, refused, GeneralId).MessageId;

        var rows = new List<Row>();
        var phase = await E2EOracles.MarkAsync(host);
        await RunAsync(host, rows, "R1 message binding, owner", honest: true, MemberId, card, correct: true);
        await RunAsync(host, rows, "R2 message binding, owner id forged", honest: false, MemberId, card, correct: false);
        await RunAsync(host, rows, "R3 message binding, other user forged", honest: false, Member2Id, card, correct: false);
        await RunAsync(host, rows, "R5 user fallback, other user's message forged", honest: false, Member2Id, publicReply, correct: false);
        await RunAsync(host, rows, "R6 no binding for the user, forged", honest: false, AdminId, publicReply, correct: false);

        // 20 minutes later the member opens help (a new private message);
        // 15 minutes after that the card's own binding has expired, and the
        // newer help binding must not revive it.
        clock.Advance(TimeSpan.FromMinutes(20));
        var help = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*monze help");
        clock.Advance(TimeSpan.FromMinutes(15));
        await RunAsync(host, rows, "R4 card binding expired, newer help open, owner", honest: true, MemberId, card, correct: false);

        // 31 minutes later every binding has expired.
        clock.Advance(TimeSpan.FromMinutes(31));
        await RunAsync(host, rows, "R7 expired binding, owner", honest: true, MemberId, card, correct: false);
        await RunAsync(host, rows, "R8 expired binding, owner id forged", honest: false, MemberId, card, correct: false);

        foreach (var row in rows)
        {
            output.WriteLine($"{row.Name}: {(row.Honest ? "honest" : "forged")} click by {(row.Actor == MemberId ? "member" : row.Actor == Member2Id ? "member2" : "admin")} -> {(row.Accepted ? "accepted" : "rejected")} (correct: {(row.Correct ? "accepted" : "rejected")})");
        }

        var inputs = rows.Select(static row => new InputExpectation(row.Click, row.Kind)).ToList();
        inputs.Add(new InputExpectation(help, ResponseKind.Ephemeral));
        await E2EOracles.AssertAsync(host, phase, new ScenarioExpectation { Inputs = inputs.OrderBy(static input => input.Input.Sequence).ToList() });

        Assert.All(rows.Where(static row => row.Honest), static row => Assert.Equal(row.Correct, row.Accepted));
        var forgedAccepted = rows.Where(static row => !row.Honest && row.Accepted).Select(static row => row.Name).ToList();
        Assert.Equal(new[] { "R2 message binding, owner id forged" }, forgedAccepted);
        KnownDefect.ExpectFailure("CAND-19", () => Assert.Empty(forgedAccepted));
        await E2EOracles.AssertInvariantsAsync(host);
    }

    private static async Task RunAsync(MonzeE2EHost host, List<Row> rows, string name, bool honest, long actor, long messageId, bool correct)
    {
        var click = honest
            ? await E2EActions.ClickAsync(host, GeneralId, messageId, actor, MeetingButtonId.Refresh)
            : await E2EActions.ForgeClickAsync(host, GeneralId, messageId, actor, MeetingButtonId.Refresh);
        var update = host.Recorder.Since(click.Sequence).SingleOrDefault(static action => action.Kind == SimActionKind.UpdateEphemeral);
        var kind = update is null ? ResponseKind.None : update.ResponseCode == 0 ? ResponseKind.Update : ResponseKind.UpdateRejected;
        rows.Add(new Row(name, honest, actor, click, update is not null, correct, kind));
    }

    private sealed record Row(string Name, bool Honest, long Actor, SimPush Click, bool Accepted, bool Correct, ResponseKind Kind);
}
