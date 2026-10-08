using Mezon.Net.Client;
using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Monze.Ui;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Areas;

/// <summary>
/// Help: the menu depends on the caller's role (member, delegate, owner),
/// every navigation button updates the same private message, close deletes
/// it, and clicks by someone else or after the 30-minute binding expired are
/// ignored without changing anything.
/// </summary>
public sealed class HelpAreaTests
{
    private static readonly MonzeCommandOptions Options = MonzeCommandOptions.Default;

    [DbFact]
    [Req("REQ-HELP-001")]
    [Covers("cmd:help", "msg:TitleHelp", "btn:monze_help_setup", "btn:monze_help_close")]
    public async Task Help_menu_follows_the_caller_role()
    {
        await using var host = await E2EActions.StartAsync("help_roles", seedDelegate: true);
        var mark = await E2EOracles.MarkAsync(host);

        var member = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*monze help");
        var admin = await E2EActions.CommandAsync(host, GeneralId, AdminId, "*monze help");
        var owner = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*monze help");

        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of(
            (member, ResponseKind.Ephemeral),
            (admin, ResponseKind.Ephemeral),
            (owner, ResponseKind.Ephemeral)));
        AssertHelp(host, member, MonzeHelpCatalog.Commands(Options, isAdmin: false, canManageWelcome: false, isOwner: false), ownerButtons: false);
        AssertHelp(host, admin, MonzeHelpCatalog.Commands(Options, isAdmin: true, canManageWelcome: true, isOwner: false), ownerButtons: false);
        AssertHelp(host, owner, MonzeHelpCatalog.Commands(Options, isAdmin: true, canManageWelcome: true, isOwner: true), ownerButtons: true);
    }

    [DbFact]
    [Req("REQ-HELP-001")]
    [Covers("btn:monze_help_meeting", "btn:monze_help_summary", "btn:monze_help_welcome", "btn:monze_help_role", "btn:monze_help_ai", "btn:monze_help_setup", "btn:monze_help_close")]
    public async Task Every_help_button_updates_the_same_message_and_close_deletes_it()
    {
        await using var host = await E2EActions.StartAsync("help_nav");
        var mark = await E2EOracles.MarkAsync(host);
        var help = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*monze help");
        var card = E2EActions.NewMessageAfter(host, help, GeneralId).MessageId;
        var expectations = new List<InputExpectation> { new(help, ResponseKind.Ephemeral) };
        var pages = new (string Button, string Page)[]
        {
            (MonzeButtonId.HelpMeeting, MonzeCommandNames.Meeting),
            (MonzeButtonId.HelpSummary, MonzeCommandNames.Summary),
            (MonzeButtonId.HelpWelcome, MonzeCommandNames.Welcome),
            (MonzeButtonId.HelpRole, MonzeCommandNames.Role),
            (MonzeButtonId.HelpAi, MonzeCommandNames.Ai),
            (MonzeButtonId.HelpSetup, MonzeCommandNames.Setup)
        };

        foreach (var (button, page) in pages)
        {
            var click = await E2EActions.ClickAsync(host, GeneralId, card, OwnerId, button);
            expectations.Add(new(click, ResponseKind.Update));
            var shown = Content(E2EActions.LastUpdateAfter(host, click, card));
            var embed = Assert.Single(shown.Embeds!);
            Assert.Equal(MonzeMessages.TitleHelp, embed.Title);
            Assert.Equal(
                MonzeHelpCatalog.ForModule(page, Options, isAdmin: true, canManageWelcome: true, isOwner: true).Select(static entry => entry.Name),
                embed.Fields!.Select(static field => field.Name));
            Assert.Contains(MonzeButtonId.HelpClose, Buttons(shown));
        }

        var close = await E2EActions.ClickAsync(host, GeneralId, card, OwnerId, MonzeButtonId.HelpClose);
        expectations.Add(new(close, ResponseKind.Delete));

        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { Inputs = expectations });
        Assert.True(host.World.FindMessage(card)!.Deleted);
        Assert.Equal(1, host.World.MessagesIn(GeneralId).Count(static message => message.IsEphemeral));
    }

    [DbFact]
    [Req("REQ-HELP-001")]
    [Covers("btn:monze_help_meeting")]
    public async Task Clicks_on_someone_elses_help_or_after_the_binding_expired_do_nothing()
    {
        var clock = new ManualCacheClock();
        await using var host = await E2EActions.StartAsync("help_foreign", services: clock.Install);
        var start = await E2EOracles.MarkAsync(host);
        var help = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*monze help");
        var card = E2EActions.NewMessageAfter(host, help, GeneralId).MessageId;
        await E2EOracles.AssertAsync(host, start, ScenarioExpectation.Of((help, ResponseKind.Ephemeral)));

        var mark = await E2EOracles.MarkAsync(host, snapshot: true);
        var foreign = await E2EActions.ClickAsync(host, GeneralId, card, Member2Id, MonzeButtonId.HelpMeeting, allowInvisible: true);
        var foreignClose = await E2EActions.ClickAsync(host, GeneralId, card, Member2Id, MonzeButtonId.HelpClose, allowInvisible: true);
        clock.Advance(TimeSpan.FromMinutes(31));
        var expired = await E2EActions.ClickAsync(host, GeneralId, card, MemberId, MonzeButtonId.HelpMeeting);

        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation
        {
            Inputs = [new(foreign, ResponseKind.None), new(foreignClose, ResponseKind.None), new(expired, ResponseKind.None)],
            Unauthorized = true
        });
        var original = host.World.FindMessage(card)!;
        Assert.False(original.Deleted);
        Assert.False(original.Updated);
    }

    private static void AssertHelp(MonzeE2EHost host, SimPush command, IReadOnlyList<MonzeHelpEntry> expected, bool ownerButtons)
    {
        var reply = E2EActions.NewMessageAfter(host, command, GeneralId);
        Assert.Equal(new[] { host.Recorder.Actions.Single(action => action.Sequence == command.Sequence).TargetUserId }, reply.ReceiverIds);
        var content = Content(reply);
        var embed = Assert.Single(content.Embeds!);
        Assert.Equal(MonzeMessages.TitleHelp, embed.Title);
        Assert.Equal(expected.Select(static entry => (entry.Name, entry.Value)), embed.Fields!.Select(static field => (field.Name, field.Value)));
        var buttons = Buttons(content);
        Assert.Equal(ownerButtons, buttons.Contains(MonzeButtonId.HelpSetup));
        Assert.Contains(MonzeButtonId.HelpMeeting, buttons);
        Assert.Contains(MonzeButtonId.HelpClose, buttons);
    }

    private static MessageContent Content(SimAction action) => MessageContent.Parse(action.ContentJson!);

    private static IReadOnlyList<string> Buttons(MessageContent content)
        => (content.Components ?? []).SelectMany(static row => row.Components).OfType<ButtonMessageComponent>().Select(static button => button.Id).ToList();
}
