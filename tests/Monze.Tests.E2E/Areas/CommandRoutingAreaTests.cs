using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Monze.Ui;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Areas;

/// <summary>
/// Routing of unrecognised input and clans Monze does not know: an unknown
/// sub-command of the root gets the documented hint; an unknown prefixed
/// command, plain chat and a direct message (clan 0) get no answer; a clan
/// the bot joined after start-up is answered, but no one there has admin
/// rights because the clan registry is only refreshed at start-up.
/// </summary>
public sealed class CommandRoutingAreaTests
{
    [DbFact]
    [Req("REQ-CMD-001")]
    [Covers("cmd:monze", "msg:UnknownCommand", "msg:TitleMonze")]
    public async Task Unknown_input_gets_the_documented_hint_or_no_answer()
    {
        await using var host = await E2EActions.StartAsync("routing_unknown");
        var mark = await E2EOracles.MarkAsync(host, snapshot: true);

        var unknownModule = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*monze dance");
        var unknownCommand = await E2EActions.IgnoredAsync(host, () => host.Inbound.SayAsync(ClanId, GeneralId, MemberId, "*dance now"));
        var chat = await E2EActions.IgnoredAsync(host, () => host.Inbound.SayAsync(ClanId, GeneralId, MemberId, "hello monze"));
        var direct = await E2EActions.IgnoredAsync(host, () => host.Inbound.SayDirectAsync(DirectId, MemberId, "*monze help"));
        Assert.Equal(1, direct.DeliveredSessions);
        // The SDK resolves the direct channel (clan 0) before giving up.
        await host.Recorder.WaitForAsync(static action => action.Operation == SimOperations.ListChannelDetail && action.ChannelId == DirectId, E2EOracles.Timeout, mark.Sequence);
        await Task.Delay(300);

        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation
        {
            Inputs = ScenarioExpectation.Of(
                (unknownModule, ResponseKind.Reply),
                (unknownCommand, ResponseKind.None),
                (chat, ResponseKind.None),
                (direct, ResponseKind.None)).Inputs,
            Unauthorized = true
        });
        var hint = E2EContent.Parse(E2EActions.NewMessageAfter(host, unknownModule, GeneralId));
        Assert.Equal(MonzeMessages.TitleMonze, hint.Embeds![0].Title);
        Assert.Equal(MonzeMessages.UnknownCommand(MonzeCommandOptions.Default), hint.Embeds![0].Fields![0].Value);
    }

    [DbFact]
    [Req("REQ-CMD-001", "REQ-CONN-001")]
    [Covers("msg:OwnerOnly")]
    public async Task A_clan_joined_after_start_up_is_answered_without_admin_rights()
    {
        await using var host = await E2EActions.StartAsync("routing_late_clan");
        var join = await E2EOracles.MarkAsync(host);
        var added = await host.Inbound.UserAddedAsync(LateClanId, BotId, isBot: true);
        Assert.Equal(1, added.DeliveredSessions);
        await host.Recorder.WaitForAsync(static action => action.Kind == SimActionKind.ClanJoin && action.ClanId == LateClanId && action.ResponseCode == 0, E2EOracles.Timeout, join.Sequence);

        var mark = await E2EOracles.MarkAsync(host, snapshot: true);
        var help = await E2EActions.CommandAsync(host, LateGeneralId, LateOwnerId, "*monze help", clanId: LateClanId);
        var setup = await E2EActions.CommandAsync(host, LateGeneralId, LateOwnerId, "*setup admin add @owner", [OwnerId], clanId: LateClanId);

        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation
        {
            Inputs = ScenarioExpectation.Of((help, ResponseKind.Ephemeral), (setup, ResponseKind.Reply)).Inputs,
            Unauthorized = true
        });
        var menu = E2EActions.NewMessageAfter(host, help, LateGeneralId);
        Assert.DoesNotContain(MonzeButtonId.HelpSetup, E2EContent.Buttons(menu));
        Assert.Contains(MonzeMessages.OwnerOnly, E2EContent.Visible(E2EActions.NewMessageAfter(host, setup, LateGeneralId)));
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM clan_registry WHERE clan_id = @clan;", ("clan", LateClanId)));
    }
}
