using System.Diagnostics;
using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using Xunit.Abstractions;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Workflows;

/// <summary>
/// Pre-registered scale defects, reproduced end to end:
/// DEF-04 (one welcome worker for every clan: a slow welcome in clan A
/// delays clan B) and DEF-06 (clan discovery reads one page of at most 100
/// clans, so a bot in 150 clans never joins 50 of them).
/// </summary>
public sealed class ScaleDefectWorkflowTests(ITestOutputHelper output)
{
    /// <summary>
    /// DEF-04: Features/Welcome/MonzeBot.WelcomeIngress.cs consumes one
    /// bounded channel with a single reader (MonzeBot.cs, SingleReader = true)
    /// and awaits every lookup and send inline, so clan B's welcome waits for
    /// clan A's 4 s member lookup. Correct behaviour asserted: clan B is
    /// welcomed within 1.5 s of its join.
    /// </summary>
    [DbFact]
    [Req("REQ-WEL-003")]
    public async Task A_slow_welcome_in_one_clan_delays_another_clan_DEF_04()
    {
        await using var host = await WelcomeWorkflowTests.StartWithWelcomeAsync("wf_def04");
        var setup = await E2EOracles.MarkAsync(host);
        var otherOn = await E2EActions.CommandAsync(host, OtherGeneralId, OutsiderId, "*welcome on", clanId: OtherClanId);
        await E2EOracles.AssertAsync(host, setup, ScenarioExpectation.Of((otherOn, ResponseKind.Ephemeral)));

        var mark = await E2EOracles.MarkAsync(host);
        host.Simulator.Faults.Delay(SimOperations.ListClanUsers, TimeSpan.FromSeconds(4));
        await host.Inbound.UserAddedAsync(ClanId, JoinerId);

        // The SDK dispatches pushes without ordering; join B once A's welcome is under way.
        await E2EActions.WaitForLogsAsync(host, static entry => entry.Message.StartsWith("Welcome target channel resolved", StringComparison.Ordinal), 1);
        var joinedB = Stopwatch.StartNew();
        await host.Inbound.UserAddedAsync(OtherClanId, Member2Id);
        var welcomeB = await host.Recorder.WaitForAsync(static action => action.Kind == SimActionKind.SendMessage && action.ClanId == OtherClanId, TimeSpan.FromSeconds(15), mark.Sequence);
        var latency = joinedB.Elapsed;
        await host.Recorder.WaitForAsync(static action => action.Kind == SimActionKind.SendMessage && action.ClanId == ClanId, TimeSpan.FromSeconds(15), mark.Sequence);
        output.WriteLine($"clan B welcome latency: {latency.TotalMilliseconds:0} ms");

        KnownDefect.ExpectFailure("DEF-04", () => Assert.True(latency < TimeSpan.FromSeconds(1.5), $"Clan B waited {latency.TotalMilliseconds:0} ms."));
        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { OtherOutputs = 2 });
        Assert.Equal(OtherGeneralId, welcomeB.ChannelId);
        Assert.Equal(MonzeMessages.DefaultWelcomeText, E2EContent.Parse(welcomeB).Text);
        await E2EOracles.AssertInvariantsAsync(host);
    }

    /// <summary>
    /// DEF-06: RefreshClansAsync (Hosting/MonzeBot.Connection.cs) lists clans
    /// once with the platform's page size and has no paging, and the clan
    /// list is only read at start-up. The simulator caps ListClanDescs at
    /// ClanDiscoveryLimit = 100 like the platform (docs/capacity-gate.md).
    /// It is worse than "50 never joined": a list of 100 looks capped
    /// (Monze.Domain/ClanDiscovery.cs ListLooksIncomplete, AssumedClanCap =
    /// 100), and Merge files every clan the registry does not know yet as
    /// IncompleteList (lines 31-35), which ApplyClanScanAsync skips. On a
    /// fresh database no clan is registered at all, so even the owner of a
    /// joined clan is refused every admin command. Correct behaviour
    /// asserted: all 150 clans are registered and joined, and the owner of a
    /// joined clan may switch its welcome on.
    /// </summary>
    [DbFact]
    [Req("REQ-CONN-001")]
    public async Task A_bot_in_150_clans_never_joins_50_of_them_DEF_06()
    {
        const int clans = 150;
        const long firstClan = 1_840_000_000_100_000_000L;
        const long firstChannel = 1_840_000_000_200_000_000L;
        var world = new SimWorld()
            .WithBot(BotId, "monze-area", E2ECanaries.MezonToken, "Monze")
            .User(OwnerId, "owner", "Clan Owner");
        for (var i = 0; i < clans; i++)
        {
            world.Clan(firstClan + i, $"Scale Clan {i:000}", OwnerId)
                .TextChannel(firstChannel + i, "general");
        }

        await using var host = await E2EActions.StartAsync("wf_def06", world);
        var mark = await E2EOracles.MarkAsync(host);
        var session = Assert.Single(host.OwnSessions);
        var registered = await host.ScalarAsync<long>("SELECT count(*) FROM clan_registry;");
        output.WriteLine($"registered {registered}, joined {session.JoinedClans.Count} of {clans}");
        var on = await E2EActions.CommandAsync(host, firstChannel, OwnerId, "*welcome on", clanId: firstClan);
        var answer = E2EContent.Visible(E2EActions.NewMessageAfter(host, on, firstChannel));

        KnownDefect.ExpectFailure("DEF-06", () =>
        {
            Assert.Equal(clans, registered);
            Assert.Equal(clans, session.JoinedClans.Count);
            Assert.Contains(MonzeMessages.WelcomeEnabled, answer);
        });
        Assert.Equal(0L, registered);
        Assert.Equal(100, session.JoinedClans.Count);
        Assert.Contains(MonzeMessages.WelcomeAdminOnly, answer);
        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of((on, ResponseKind.Reply)));
        await E2EOracles.AssertInvariantsAsync(host);
    }
}
