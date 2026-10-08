using Mezon.Net.Core;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Workflows;

/// <summary>
/// Welcome delivery under faults (Features/Welcome/MonzeBot.WelcomeIngress.cs):
/// a failed send releases the welcome_delivery claim so the next join event
/// delivers exactly once; and the D6 regression
/// (docs/test-artifacts/20261007-two-account-run.md row D6, fix 4f6d072):
/// when one target lookup (members, roles or channels) fails, only that
/// lookup's placeholders stay as text and every other placeholder still
/// resolves to a mention or hashtag.
/// </summary>
public sealed class WelcomeWorkflowTests
{
    private const string Template = "Chào {user}! {user:member-nick} ơi, đọc {channel:general} và nhận {role:Developer}.";
    private const long FirstJoinerId = 1_840_000_000_000_000_291L;
    private const long SecondJoinerId = 1_840_000_000_000_000_292L;
    private const long ThirdJoinerId = 1_840_000_000_000_000_293L;

    [DbFact]
    [Req("REQ-WEL-003")]
    public async Task A_failed_welcome_send_releases_the_claim_and_the_next_join_delivers_once()
    {
        await using var host = await StartWithWelcomeAsync("wf_welcome_retry", simulatorOptions: new MezonSimulatorOptions { SocketTimeoutMilliseconds = 2000 });
        var mark = await E2EOracles.MarkAsync(host);

        // The socket closes under the send: the SDK call throws, the claim is released.
        host.Simulator.Faults.CloseSocket(SimOperations.ChannelMessageSend);
        await host.Inbound.UserAddedAsync(ClanId, JoinerId);
        await E2EActions.WaitForLogsAsync(host, static entry => entry.Message.StartsWith("Welcome worker failed", StringComparison.Ordinal), 1);
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM welcome_delivery WHERE clan_id = @clan;", ("clan", ClanId)));
        await E2EActions.WaitUntilAsync(host, () => Task.FromResult(host.OwnSessions.Any(static session => session.IsConnected && session.HasJoinedClan(ClanId))), "the reconnect");

        await host.Inbound.UserAddedAsync(ClanId, JoinerId);
        await E2EActions.WaitForLogsAsync(host, static entry => entry.Message.StartsWith("Welcome message sent", StringComparison.Ordinal), 1);
        await host.Inbound.UserAddedAsync(ClanId, JoinerId);
        await E2EActions.WaitForLogsAsync(host, static entry => entry.Message.StartsWith("Welcome event ignored because delivery was already claimed", StringComparison.Ordinal), 1);

        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { OtherOutputs = 1, AllowedWarnings = ["Welcome worker failed.", "Mezon socket disconnected"] });
        var welcome = Assert.Single(host.Recorder.Since(mark.Sequence), static action => action.Kind == SimActionKind.SendMessage);
        Assert.Equal("Chào @New Joiner! @member-nick ơi, đọc #general và nhận @Developer.", E2EContent.Parse(welcome).Text);
        Assert.Single(host.World.MessagesIn(LobbyId), message => message.SenderId == BotId);
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM welcome_delivery WHERE clan_id = @clan AND user_id = @user;", ("clan", ClanId), ("user", JoinerId)));
        await E2EOracles.AssertInvariantsAsync(host);
    }

    /// <summary>
    /// Regression for WF-01: a send the platform rejects is answered with an
    /// error envelope carrying the request cid; Mezon.Net.Sdk 1.6.2 completes
    /// the ack with it and returns ChannelMessageAckResponse(null) instead of
    /// throwing (Generated/BaseMezonSocketClient.Realtime.g.cs
    /// SendChatMessageRtAsync). The welcome worker used to log "Welcome
    /// message sent" and keep the claim, so the member was never welcomed.
    /// An ack without a message id now releases the claim, and the next join
    /// welcomes the member once.
    /// </summary>
    [DbFact]
    [Req("REQ-WEL-003")]
    public async Task A_rejected_welcome_send_releases_the_claim_and_the_next_join_is_welcomed()
    {
        await using var host = await StartWithWelcomeAsync("wf_welcome_rejected");
        var mark = await E2EOracles.MarkAsync(host);
        host.Simulator.Faults.Fail(SimOperations.ChannelMessageSend, MezonStatusCode.Internal);
        await host.Inbound.UserAddedAsync(ClanId, JoinerId);
        await E2EActions.WaitForLogsAsync(host, static entry => entry.Message.StartsWith("Welcome worker failed", StringComparison.Ordinal), 1);
        await host.Inbound.UserAddedAsync(ClanId, JoinerId);
        await E2EActions.WaitForLogsAsync(host, static entry => entry.Message.StartsWith("Welcome message sent", StringComparison.Ordinal), 1);

        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { OtherOutputs = 2, AllowedWarnings = ["Welcome worker failed"] });
        var sends = host.Recorder.Since(mark.Sequence).Where(static action => action.Kind == SimActionKind.SendMessage).ToList();
        Assert.Equal(new[] { (int)MezonStatusCode.Internal, 0 }, sends.Select(static action => action.ResponseCode));
        Assert.Single(host.World.MessagesIn(LobbyId), message => message.SenderId == BotId);
        await E2EOracles.AssertInvariantsAsync(host);
    }

    [DbFact]
    [Req("REQ-WEL-003")]
    public async Task One_failing_lookup_leaves_only_its_own_placeholders_unresolved_D6()
    {
        await using var host = await StartWithWelcomeAsync("wf_welcome_d6");
        host.World.User(FirstJoinerId, "joiner-one", "Joiner One")
            .User(SecondJoinerId, "joiner-two", "Joiner Two")
            .User(ThirdJoinerId, "joiner-three", "Joiner Three");
        var mark = await E2EOracles.MarkAsync(host);
        var cases = new (long User, string Operation, string Expected)[]
        {
            (FirstJoinerId, SimOperations.ListClanUsers, "Chào @Joiner One! {user:member-nick} ơi, đọc #general và nhận @Developer."),
            (SecondJoinerId, SimOperations.ListRoles, "Chào @Joiner Two! @member-nick ơi, đọc #general và nhận {role:Developer}."),
            (ThirdJoinerId, SimOperations.ListChannelDescs, "Chào @Joiner Three! @member-nick ơi, đọc {channel:general} và nhận @Developer.")
        };
        var sent = 0;
        foreach (var (user, operation, _) in cases)
        {
            host.Simulator.Faults.Fail(operation, MezonStatusCode.Unavailable);
            await host.Inbound.UserAddedAsync(ClanId, user);
            await E2EActions.WaitForLogsAsync(host, static entry => entry.Message.StartsWith("Welcome message sent", StringComparison.Ordinal), ++sent);
            Assert.Empty(host.Simulator.Faults.Pending);
        }

        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation
        {
            OtherOutputs = 3,
            AllowedWarnings = ["Welcome member lookup failed", "Welcome role lookup failed", "Welcome channel lookup failed"]
        });
        var welcomes = host.Recorder.Since(mark.Sequence).Where(static action => action.Kind == SimActionKind.SendMessage).ToList();
        for (var i = 0; i < cases.Length; i++)
        {
            var content = E2EContent.Parse(welcomes[i]);
            Assert.Equal(cases[i].Expected, content.Text);
            Assert.Contains(welcomes[i].Mentions, mention => mention.UserId == cases[i].User);
            Assert.Equal(i != 0, welcomes[i].Mentions.Any(static mention => mention.UserId == MemberId));
            Assert.Equal(i != 1, welcomes[i].Mentions.Any(static mention => mention.RoleId == DeveloperRoleId));
            Assert.Equal(i != 2, (content.Hashtags ?? []).Any(static hashtag => hashtag.ChannelId == GeneralId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        Assert.Single(host.Logs.Entries, static entry => entry.Message.StartsWith("Welcome member lookup failed", StringComparison.Ordinal));
        Assert.Single(host.Logs.Entries, static entry => entry.Message.StartsWith("Welcome role lookup failed", StringComparison.Ordinal));
        Assert.Single(host.Logs.Entries, static entry => entry.Message.StartsWith("Welcome channel lookup failed", StringComparison.Ordinal));
        await E2EOracles.AssertInvariantsAsync(host);
    }

    /// <summary>Starts Monze and switches clan A's welcome on with the placeholder template.</summary>
    internal static async Task<MonzeE2EHost> StartWithWelcomeAsync(string tag, SimWorld? world = null, MezonSimulatorOptions? simulatorOptions = null)
    {
        var host = await E2EActions.StartAsync(tag, world, simulatorOptions: simulatorOptions);
        try
        {
            var mark = await E2EOracles.MarkAsync(host);
            var text = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*welcome message " + Template);
            var on = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*welcome on");
            await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of((text, ResponseKind.Ephemeral), (on, ResponseKind.Ephemeral)));
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }
}
