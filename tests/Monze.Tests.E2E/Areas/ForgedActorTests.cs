using System.Text.Json;
using Monze.Application.Commands;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Monze.Ui;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Areas;

/// <summary>
/// Known gap CAND-19: a click event carries a client-supplied user_id.
/// mezon-api MessageButtonClick (server/api_interactive_message.go) forwards
/// the client's MessageButtonClicked unchanged (DropdownBoxSelected right
/// below overwrites user_id with the session user), Mezon.Net.Sdk 1.6.2
/// InteractionRouter marks every click ServerAuthenticated, and Monze's
/// EnsurePrivateInteractionAsync (Hosting/MonzeBot.InteractionResponses.cs)
/// falls back to the (clan, channel, user) binding when the message id has
/// none. While the owner has a welcome setup open in a channel, any member
/// can send WelcomeSave with user_id = owner, any message id and their own
/// extra_data, and EnsureWelcomeAdministratorAsync checks the forged id.
/// </summary>
public sealed class ForgedActorTests
{
    [DbFact]
    [Req("REQ-INBOX-002", "REQ-WEL-002")]
    [Covers("btn:monze_welcome_save", "msg:WelcomeEmbedSaved")]
    public async Task A_forged_welcome_save_with_the_owners_id_changes_nothing()
    {
        await using var host = await E2EActions.StartAsync("forged_actor");
        var start = await E2EOracles.MarkAsync(host);
        var setup = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*welcome setup");
        var form = E2EActions.NewMessageAfter(host, setup, GeneralId).MessageId;
        var chat = await E2EActions.IgnoredAsync(host, () => host.Inbound.SayAsync(ClanId, GeneralId, Member2Id, "nice bot"));
        await E2EOracles.AssertAsync(host, start, ScenarioExpectation.Of((setup, ResponseKind.Ephemeral), (chat, ResponseKind.None)));

        // Member 2 cannot see the owner's ephemeral form; the forged event
        // names a message member 2 can see, the bot as sender and the owner as user.
        var attack = await E2EOracles.MarkAsync(host, snapshot: true);
        var forged = await E2EActions.ForgeClickAsync(host, GeneralId, chat.MessageId, OwnerId, MonzeButtonId.WelcomeSave, Form("Hijacked by member2"));
        await KnownDefect.ExpectFailureAsync("CAND-19", () => E2EOracles.AssertAsync(host, attack, new ScenarioExpectation
        {
            Inputs = ScenarioExpectation.Of((forged, ResponseKind.None)).Inputs,
            Unauthorized = true
        }));

        // Control: the same click from the owner's own client saves, so the
        // expected failure above is about the forgery, not a broken flow.
        var control = await E2EOracles.MarkAsync(host);
        var save = await E2EActions.ClickAsync(host, GeneralId, form, OwnerId, MonzeButtonId.WelcomeSave, Form("Owner title"));
        await E2EOracles.AssertAsync(host, control, ScenarioExpectation.Of((save, ResponseKind.Update)));
        Assert.Contains(MonzeMessages.WelcomeEmbedSaved, E2EContent.Visible(E2EActions.LastUpdateAfter(host, save, form)));
        var embed = await host.ScalarAsync<string>("SELECT welcome_embed::text FROM clan_settings WHERE clan_id = @clan;", ("clan", ClanId));
        using var saved = JsonDocument.Parse(embed!);
        Assert.Equal("Owner title", saved.RootElement.GetProperty("Title").GetString());
    }

    private static string Form(string title)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["data"] = new Dictionary<string, object>
            {
                ["monze_welcome_status"] = new Dictionary<string, string> { ["value"] = "on" },
                ["monze_welcome_title"] = new Dictionary<string, string> { ["value"] = title }
            }
        });
}
