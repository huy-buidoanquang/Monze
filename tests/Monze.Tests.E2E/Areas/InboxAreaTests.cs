using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Monze.Ui;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Areas;

/// <summary>
/// Idempotency: a command message the platform delivers twice is answered
/// once and claimed once in command_inbox; a "start now" click delivered
/// twice suggests one meeting and is claimed once in interaction_inbox.
/// </summary>
public sealed class InboxAreaTests
{
    [DbFact]
    [Req("REQ-INBOX-001")]
    public async Task A_duplicated_command_push_is_answered_once()
    {
        await using var host = await E2EActions.StartAsync("inbox_command");
        var mark = await E2EOracles.MarkAsync(host);
        host.Simulator.Faults.DuplicatePush(SimPushKind.ChannelMessage);

        var help = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*monze help");

        Assert.Equal(SimFaultKind.DuplicatePush, help.Fault);
        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of((help, ResponseKind.Ephemeral)));
        Assert.Equal(1L, await host.ScalarAsync<long>(
            "SELECT count(*) FROM command_inbox WHERE channel_id = @channel AND message_id = @message;",
            ("channel", GeneralId),
            ("message", help.MessageId)));
    }

    [DbFact]
    [Req("REQ-INBOX-002", "REQ-MTG-001")]
    [Covers("btn:monze_meeting_now")]
    public async Task A_duplicated_start_now_click_suggests_one_meeting()
    {
        await using var host = await E2EActions.StartAsync("inbox_click");
        var mark = await E2EOracles.MarkAsync(host);
        var list = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*meeting");
        var card = E2EActions.NewMessageAfter(host, list, GeneralId).MessageId;
        host.Simulator.Faults.DuplicatePush(SimPushKind.MessageButtonClicked);

        var click = await E2EActions.ClickAsync(host, GeneralId, card, MemberId, MeetingButtonId.StartNow);
        await E2EActions.WaitForLogsAsync(
            host,
            entry => entry.Message.StartsWith("Button interaction dispatch", StringComparison.Ordinal)
                && entry.Message.Contains($"Message={card},", StringComparison.Ordinal)
                && entry.Message.Contains($"CustomId={MeetingButtonId.StartNow},", StringComparison.Ordinal),
            2);

        Assert.Equal(SimFaultKind.DuplicatePush, click.Fault);
        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of((list, ResponseKind.Ephemeral), (click, ResponseKind.UpdateAndPublic)));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM meeting_session WHERE clan_id = @clan;", ("clan", ClanId)));
        Assert.Equal("completed", await host.ScalarAsync<string>(
            "SELECT status FROM interaction_inbox WHERE channel_id = @channel AND message_id = @message AND action = @action;",
            ("channel", GeneralId),
            ("message", card),
            ("action", MeetingButtonId.StartNow)));
    }
}
