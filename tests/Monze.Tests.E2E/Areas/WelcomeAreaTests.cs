using System.Text.Json;
using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Monze.Ui;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Areas;

/// <summary>
/// Welcome: commands switch it on and off and store the text; the embed form
/// is edited through buttons and extra_data, previewed, saved once per draft
/// token and only on the configuration version it was opened on; a human
/// joining gets exactly one welcome in the welcome channel with resolved
/// mentions, a bot gets none, and a duplicated join push still gives one.
/// </summary>
public sealed class WelcomeAreaTests
{
    private const string Template = "Chào {user}! Đọc {channel:general} và nhận {role:Developer}.";

    [DbFact]
    [Req("REQ-WEL-001")]
    [Covers("cmd:welcome", "msg:WelcomeEnabled", "msg:WelcomeDisabled", "msg:WelcomeMessageSaved", "msg:WelcomeAdminOnly", "msg:WelcomePreview")]
    public async Task Welcome_commands_switch_it_and_store_the_text_for_admins_only()
    {
        await using var host = await E2EActions.StartAsync("welcome_cmd");
        var mark = await E2EOracles.MarkAsync(host);
        var on = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*welcome on");
        var text = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*welcome message " + Template);
        var preview = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*monze welcome preview");
        var off = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*welcome off");
        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of(
            (on, ResponseKind.Ephemeral),
            (text, ResponseKind.Ephemeral),
            (preview, ResponseKind.Ephemeral),
            (off, ResponseKind.Ephemeral)));
        Assert.Contains(MonzeMessages.WelcomeEnabled, Answer(host, on));
        Assert.Contains(MonzeMessages.WelcomeMessageSaved, Answer(host, text));
        Assert.Contains(Template, Answer(host, preview));
        Assert.Contains(MonzeMessages.WelcomeDisabled, Answer(host, off));
        Assert.Contains(MonzeButtonId.WelcomeSettings, E2EContent.Buttons(E2EActions.NewMessageAfter(host, on, GeneralId)));
        var stored = Assert.Single(await host.RowsAsync(
            "SELECT welcome_enabled, welcome_text, version FROM clan_settings WHERE clan_id = @clan;",
            ("clan", ClanId)));
        Assert.Equal(false, stored[0]);
        Assert.Equal(Template, stored[1]);
        Assert.Equal(4L, stored[2]);

        var refused = await E2EOracles.MarkAsync(host, snapshot: true);
        var memberOn = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*welcome on");
        var memberSetup = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*welcome setup");
        await E2EOracles.AssertAsync(host, refused, new ScenarioExpectation
        {
            Inputs = ScenarioExpectation.Of((memberOn, ResponseKind.Reply), (memberSetup, ResponseKind.Reply)).Inputs,
            Unauthorized = true
        });
        Assert.Contains(MonzeMessages.WelcomeAdminOnly, Answer(host, memberOn));
        Assert.Contains(MonzeMessages.WelcomeAdminOnly, Answer(host, memberSetup));
    }

    [DbFact]
    [Req("REQ-WEL-002")]
    [Covers("btn:monze_welcome_images", "btn:monze_welcome_preview", "btn:monze_welcome_save", "btn:monze_welcome_cancel", "msg:WelcomeEmbedSaved", "msg:WelcomeDraftInvalid", "msg:WelcomeConfigurationChanged", "msg:WelcomeSetupCancelled", "msg:TitleWelcomeSetup")]
    public async Task Welcome_form_previews_and_saves_once_on_the_version_it_was_opened_on()
    {
        await using var host = await E2EActions.StartAsync("welcome_form");
        var mark = await E2EOracles.MarkAsync(host);
        var inputs = new List<InputExpectation>();

        var setup = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*welcome setup");
        inputs.Add(new(setup, ResponseKind.Ephemeral));
        var form = E2EActions.NewMessageAfter(host, setup, GeneralId);
        Assert.Equal(MonzeMessages.TitleWelcomeSetup, E2EContent.Parse(form).Embeds![0].Title);
        Assert.Contains(MonzeButtonId.WelcomeSave, E2EContent.Buttons(form));
        var card = form.MessageId;

        var images = await E2EActions.ClickAsync(host, GeneralId, card, OwnerId, MonzeButtonId.WelcomeImages, Form(("monze_welcome_thumbnail", "https://cdn.example.test/thumb.png")));
        inputs.Add(new(images, ResponseKind.Update));
        Assert.Contains("Ảnh nhỏ", E2EContent.Visible(E2EActions.LastUpdateAfter(host, images, card)));

        var preview = await E2EActions.ClickAsync(host, GeneralId, card, OwnerId, MonzeButtonId.WelcomePreview, Form(
            ("monze_welcome_status", "on"),
            ("monze_welcome_title", "Chào thành viên mới"),
            ("monze_welcome_description", "Xin chào {user}"),
            ("monze_welcome_color", "#22aa55")));
        inputs.Add(new(preview, ResponseKind.Update));
        var previewCard = E2EActions.LastUpdateAfter(host, preview, card);
        Assert.Equal("Chào thành viên mới", E2EContent.Parse(previewCard).Embeds![0].Title);
        var submit = Assert.Single(E2EContent.Buttons(previewCard), static id => id.StartsWith(MonzeButtonId.WelcomeSavePrefix, StringComparison.Ordinal));

        var save = await E2EActions.ClickAsync(host, GeneralId, card, OwnerId, submit);
        inputs.Add(new(save, ResponseKind.Update));
        Assert.Contains(MonzeMessages.WelcomeEmbedSaved, E2EContent.Visible(E2EActions.LastUpdateAfter(host, save, card)));
        var saved = await WelcomeAsync(host);
        Assert.Equal(true, saved[0]);
        Assert.Equal(2L, saved[1]);
        using (var embed = JsonDocument.Parse((string)saved[2]!))
        {
            Assert.Equal("Chào thành viên mới", embed.RootElement.GetProperty("Title").GetString());
            Assert.Equal("Xin chào {user}", embed.RootElement.GetProperty("Description").GetString());
            Assert.Equal("#22AA55", embed.RootElement.GetProperty("Color").GetString());
            Assert.Equal("https://cdn.example.test/thumb.png", embed.RootElement.GetProperty("ThumbnailUrl").GetString());
        }

        var reuse = await E2EActions.ClickAsync(host, GeneralId, card, OwnerId, submit);
        inputs.Add(new(reuse, ResponseKind.Update));
        Assert.Contains(MonzeMessages.WelcomeDraftInvalid, E2EContent.Visible(E2EActions.LastUpdateAfter(host, reuse, card)));

        var reopen = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*welcome setup");
        inputs.Add(new(reopen, ResponseKind.Ephemeral));
        var second = E2EActions.NewMessageAfter(host, reopen, GeneralId).MessageId;
        var off = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*welcome off");
        inputs.Add(new(off, ResponseKind.Ephemeral));
        var stale = await E2EActions.ClickAsync(host, GeneralId, second, OwnerId, MonzeButtonId.WelcomeSave, Form(("monze_welcome_title", "Stale title")));
        inputs.Add(new(stale, ResponseKind.Update));
        Assert.Contains(MonzeMessages.WelcomeConfigurationChanged, E2EContent.Visible(E2EActions.LastUpdateAfter(host, stale, second)));
        var cancel = await E2EActions.ClickAsync(host, GeneralId, second, OwnerId, MonzeButtonId.WelcomeCancel);
        inputs.Add(new(cancel, ResponseKind.Update));
        Assert.Contains(MonzeMessages.WelcomeSetupCancelled, E2EContent.Visible(E2EActions.LastUpdateAfter(host, cancel, second)));

        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { Inputs = inputs });
        var final = await WelcomeAsync(host);
        Assert.Equal(false, final[0]);
        Assert.Equal(3L, final[1]);
        Assert.DoesNotContain("Stale title", (string)final[2]!, StringComparison.Ordinal);
    }

    [DbFact]
    [Req("REQ-WEL-003")]
    [Covers("msg:DefaultWelcomeText")]
    public async Task A_human_join_gets_one_welcome_a_bot_none_and_a_duplicated_join_one()
    {
        const long LateJoinerId = 1_840_000_000_000_000_299L;
        await using var host = await E2EActions.StartAsync("welcome_join");
        host.World.User(LateJoinerId, "late-joiner", "Late Joiner");
        var setupMark = await E2EOracles.MarkAsync(host);
        var text = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*welcome message " + Template);
        var on = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*welcome on");
        await E2EOracles.AssertAsync(host, setupMark, ScenarioExpectation.Of((text, ResponseKind.Ephemeral), (on, ResponseKind.Ephemeral)));

        var mark = await E2EOracles.MarkAsync(host);
        await host.Inbound.UserAddedAsync(ClanId, JoinerId);
        await E2EActions.WaitForLogsAsync(host, static entry => entry.Message.StartsWith("Welcome message sent", StringComparison.Ordinal), 1);
        await host.Inbound.UserAddedAsync(ClanId, OtherBotId, isBot: true);
        await E2EActions.WaitForLogsAsync(host, static entry => entry.Message.StartsWith("Welcome event ignored. IsBot=True", StringComparison.Ordinal), 1);
        host.Simulator.Faults.DuplicatePush(SimPushKind.AddClanUserEvent);
        var duplicated = await host.Inbound.UserAddedAsync(ClanId, LateJoinerId);
        Assert.Equal(SimFaultKind.DuplicatePush, duplicated.Fault);
        await E2EActions.WaitForLogsAsync(host, static entry => entry.Message.StartsWith("Welcome message sent", StringComparison.Ordinal), 2);
        await E2EActions.WaitForLogsAsync(host, static entry => entry.Message.StartsWith("Welcome event ignored because delivery was already claimed", StringComparison.Ordinal), 1);

        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { OtherOutputs = 2 });
        var welcomes = host.Recorder.Since(mark.Sequence).Where(static action => action.Kind == SimActionKind.SendMessage).ToList();
        Assert.Equal(2, welcomes.Count);
        Assert.All(welcomes, static welcome => Assert.Equal(LobbyId, welcome.ChannelId));
        var first = welcomes[0];
        var content = E2EContent.Parse(first);
        Assert.Equal("Chào @New Joiner! Đọc #general và nhận @Developer.", content.Text);
        Assert.Contains(first.Mentions, static mention => mention.UserId == JoinerId);
        Assert.Contains(first.Mentions, static mention => mention.RoleId == DeveloperRoleId);
        Assert.Contains(content.Hashtags!, static hashtag => hashtag.ChannelId == GeneralId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(welcomes[1].Mentions, static mention => mention.UserId == LateJoinerId);
        Assert.Equal(
            new[] { JoinerId, LateJoinerId },
            (await host.RowsAsync("SELECT user_id FROM welcome_delivery WHERE clan_id = @clan ORDER BY user_id;", ("clan", ClanId)))
                .Select(static row => (long)row[0]!));
    }

    /// <summary>extra_data as the web client sends it for embed inputs and the status radio.</summary>
    private static string Form(params (string Id, string Value)[] values)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["data"] = values.ToDictionary(static value => value.Id, static value => (object)new Dictionary<string, string> { ["value"] = value.Value })
        });

    private static string Answer(MonzeE2EHost host, SimPush command)
        => E2EContent.Visible(E2EActions.NewMessageAfter(host, command, GeneralId));

    private static async Task<object?[]> WelcomeAsync(MonzeE2EHost host)
        => Assert.Single(await host.RowsAsync(
            "SELECT welcome_enabled, version, welcome_embed::text FROM clan_settings WHERE clan_id = @clan;",
            ("clan", ClanId)));
}
