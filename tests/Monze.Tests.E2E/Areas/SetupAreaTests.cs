using Monze.Application.Commands;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Areas;

/// <summary>
/// Setup and delegates: only the owner adds or removes a delegate, a
/// delegate gets admin help, and non-owners, self-delegation and delegating
/// someone outside the clan are refused without changing anything.
/// </summary>
public sealed class SetupAreaTests
{
    [DbFact]
    [Req("REQ-SETUP-001")]
    [Covers("cmd:setup", "msg:DelegateAdded", "msg:DelegateAlreadyExists", "msg:DelegateRemoved", "msg:DelegateNotFound")]
    public async Task Owner_adds_and_removes_a_delegate()
    {
        await using var host = await E2EActions.StartAsync("setup_owner");
        var mark = await E2EOracles.MarkAsync(host);

        var add = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*setup admin add @admin", [AdminId]);
        var addAgain = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*setup admin add @admin", [AdminId]);
        Assert.Equal(1L, await DelegatesAsync(host));
        var adminHelp = await E2EActions.CommandAsync(host, GeneralId, AdminId, "*monze help");
        var remove = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*monze setup admin remove @admin", [AdminId]);
        var removeAgain = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*monze setup admin remove @admin", [AdminId]);

        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of(
            (add, ResponseKind.Reply),
            (addAgain, ResponseKind.Reply),
            (adminHelp, ResponseKind.Ephemeral),
            (remove, ResponseKind.Reply),
            (removeAgain, ResponseKind.Reply)));
        Assert.Contains(MonzeMessages.DelegateAdded, Answer(host, add));
        Assert.Contains(MonzeMessages.DelegateAlreadyExists, Answer(host, addAgain));
        Assert.Equal(
            MonzeHelpCatalog.Commands(MonzeCommandOptions.Default, isAdmin: true, canManageWelcome: true).Select(static entry => entry.Value),
            E2EContent.Parse(E2EActions.NewMessageAfter(host, adminHelp, GeneralId)).Embeds![0].Fields!.Select(static field => field.Value));
        Assert.Contains(MonzeMessages.DelegateRemoved, Answer(host, remove));
        Assert.Contains(MonzeMessages.DelegateNotFound, Answer(host, removeAgain));
        Assert.Equal(0L, await DelegatesAsync(host));
    }

    [DbFact]
    [Req("REQ-SETUP-001")]
    [Covers("msg:OwnerOnly", "msg:OwnerAlreadyAdmin", "msg:DelegateMustBeClanMember")]
    public async Task Non_owners_self_delegation_and_non_members_are_refused()
    {
        await using var host = await E2EActions.StartAsync("setup_refused", seedDelegate: true);
        var mark = await E2EOracles.MarkAsync(host, snapshot: true);

        var byMember = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*setup admin add @member2", [Member2Id]);
        var byDelegate = await E2EActions.CommandAsync(host, GeneralId, AdminId, "*setup admin remove @admin", [AdminId]);
        var self = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*setup admin add @owner", [OwnerId]);
        var outsider = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*setup admin add @outsider", [OutsiderId]);

        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation
        {
            Inputs = ScenarioExpectation.Of(
                (byMember, ResponseKind.Reply),
                (byDelegate, ResponseKind.Reply),
                (self, ResponseKind.Reply),
                (outsider, ResponseKind.Reply)).Inputs,
            Unauthorized = true
        });
        Assert.Contains(MonzeMessages.OwnerOnly, Answer(host, byMember));
        Assert.Contains(MonzeMessages.OwnerOnly, Answer(host, byDelegate));
        Assert.Contains(MonzeMessages.OwnerAlreadyAdmin, Answer(host, self));
        Assert.Contains(MonzeMessages.DelegateMustBeClanMember, Answer(host, outsider));
        Assert.Equal(1L, await DelegatesAsync(host));
    }

    private static string Answer(MonzeE2EHost host, Monze.Simulator.SimPush command)
        => E2EContent.Visible(E2EActions.NewMessageAfter(host, command, GeneralId));

    private static Task<long> DelegatesAsync(MonzeE2EHost host)
        => host.ScalarAsync<long>("SELECT count(*) FROM clan_admin WHERE clan_id = @clan;", ("clan", ClanId));
}
