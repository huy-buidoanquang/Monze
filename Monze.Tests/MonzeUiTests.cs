using System.Text.Json;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Ui;
using Xunit;

namespace Monze.Tests;

public sealed class MonzeUiTests
{
    [Fact]
    public void Status_colors_are_stable_and_info_colors_are_from_palette()
    {
        Assert.Equal(MonzeEmbedColors.Success, ReadColor(MonzeMessageBuilder.Card(
            new CommandOutcome { Title = "x", Text = "ok", Tone = MonzeTone.Ok }).RawJson));
        Assert.Equal(MonzeEmbedColors.Warning, ReadColor(MonzeMessageBuilder.Card(
            new CommandOutcome { Title = "x", Text = "warn", Tone = MonzeTone.Warn }).RawJson));
        Assert.Equal(MonzeEmbedColors.Error, ReadColor(MonzeMessageBuilder.Card(
            new CommandOutcome { Title = "x", Text = "error", Tone = MonzeTone.Error }).RawJson));

        var colors = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 40; i++)
        {
            colors.Add(ReadColor(MonzeMessageBuilder.Card(
                new CommandOutcome { Title = "x", Text = "info", Tone = MonzeTone.Info }).RawJson));
        }

        Assert.All(colors, color => Assert.True(MonzeEmbedColors.IsInformational(color)));
        Assert.True(colors.Count > 1);
    }

    [Fact]
    public void Help_uses_compact_title_and_hides_admin_commands_for_members()
    {
        var options = new MonzeCommandOptions("*", MonzeCommandNames.Monze);
        var member = Parse(MonzeMessageBuilder.HelpPage("commands", options, isAdmin: false).RawJson);
        var memberEmbed = member.GetProperty("embed")[0];

        Assert.Equal("Hướng dẫn", memberEmbed.GetProperty("title").GetString());
        Assert.DoesNotContain("Kết quả", member.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("*monze welcome on", member.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("*role", member.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("*ai", member.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("*event", member.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("*points", member.GetRawText(), StringComparison.Ordinal);

        var adminWithoutOwnership = Parse(MonzeMessageBuilder.HelpPage(
            "commands",
            options,
            isAdmin: true,
            canManageWelcome: false).RawJson);
        Assert.DoesNotContain("*monze welcome on", adminWithoutOwnership.GetRawText(), StringComparison.Ordinal);

        var admin = Parse(MonzeMessageBuilder.HelpPage("commands", options, isAdmin: true, canManageWelcome: true).RawJson);
        Assert.Contains("*welcome", admin.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Help_uses_one_embed_field_per_command_suggestion()
    {
        var options = new MonzeCommandOptions("*", MonzeCommandNames.Monze);
        var root = Parse(MonzeMessageBuilder.HelpPage("commands", options, isAdmin: true, canManageWelcome: true).RawJson);
        var fields = root.GetProperty("embed")[0].GetProperty("fields");

        Assert.InRange(fields.GetArrayLength(), 1, 25);
        foreach (var field in fields.EnumerateArray())
        {
            Assert.DoesNotContain('\n', field.GetProperty("value").GetString() ?? string.Empty);
        }

        var meeting = Parse(MonzeMessageBuilder.HelpPage("meeting", options).RawJson);
        foreach (var field in meeting.GetProperty("embed")[0].GetProperty("fields").EnumerateArray())
        {
            Assert.DoesNotContain('\n', field.GetProperty("value").GetString() ?? string.Empty);
        }

        Assert.All(root.GetProperty("embed")[0].GetProperty("fields").EnumerateArray(), field =>
        {
            var name = field.GetProperty("name").GetString() ?? string.Empty;
            Assert.DoesNotContain(" · ", name, StringComparison.Ordinal);
            Assert.StartsWith("*", name, StringComparison.Ordinal);
        });

        Assert.Equal("*welcome on|off", Parse(MonzeMessageBuilder.HelpPage(
            "welcome",
            options,
            isAdmin: true,
            canManageWelcome: true).RawJson)
            .GetProperty("embed")[0]
            .GetProperty("fields")[0]
            .GetProperty("name")
            .GetString());

        Assert.Equal("*meeting", meeting.GetProperty("embed")[0].GetProperty("fields")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Help_navigation_keeps_module_buttons_and_close_on_every_page()
    {
        var options = new MonzeCommandOptions("*", MonzeCommandNames.Monze);
        var root = MonzeMessageBuilder.HelpPage(
            "commands",
            options,
            isAdmin: true,
            canManageWelcome: true,
            isOwner: true).RawJson;
        var meeting = MonzeMessageBuilder.HelpPage(
            "meeting",
            options,
            isAdmin: true,
            canManageWelcome: true,
            isOwner: true).RawJson;
        var ai = MonzeMessageBuilder.HelpPage(
            "ai",
            options,
            isAdmin: false,
            canManageWelcome: false,
            isOwner: false).RawJson;

        foreach (var buttonId in new[]
        {
            MonzeButtonId.HelpMeeting,
            MonzeButtonId.HelpSummary,
            MonzeButtonId.HelpWelcome,
            MonzeButtonId.HelpRole,
            MonzeButtonId.HelpAi,
            MonzeButtonId.HelpClose
        })
        {
            Assert.Contains(buttonId, root, StringComparison.Ordinal);
            Assert.Contains(buttonId, meeting, StringComparison.Ordinal);
            Assert.Contains(buttonId, ai, StringComparison.Ordinal);
        }

        Assert.Contains(MonzeButtonId.HelpSetup, root, StringComparison.Ordinal);
        Assert.Contains(MonzeButtonId.HelpSetup, meeting, StringComparison.Ordinal);
        Assert.DoesNotContain(MonzeButtonId.HelpSetup, ai, StringComparison.Ordinal);
        Assert.DoesNotContain("Chung", root, StringComparison.Ordinal);
        Assert.DoesNotContain("Chung", meeting, StringComparison.Ordinal);
        Assert.DoesNotContain("Chung", ai, StringComparison.Ordinal);
    }

    [Fact]
    public void Structured_outcome_fields_render_as_separate_embed_fields()
    {
        var content = MonzeMessageBuilder.Card(new CommandOutcome
        {
            Title = "AI",
            Text = string.Empty,
            Fields =
            [
                new CommandField("Tóm tắt", "Nội dung đã tóm tắt."),
                new CommandField("Lưu ý", "Có thể thiếu một phần lịch sử.")
            ]
        });

        var fields = Parse(content.RawJson).GetProperty("embed")[0].GetProperty("fields");

        Assert.Equal(2, fields.GetArrayLength());
        Assert.Equal("Tóm tắt", fields[0].GetProperty("name").GetString());
        Assert.Equal("Nội dung đã tóm tắt.", fields[0].GetProperty("value").GetString());
        Assert.Equal("Lưu ý", fields[1].GetProperty("name").GetString());
        Assert.Equal("Có thể thiếu một phần lịch sử.", fields[1].GetProperty("value").GetString());
        Assert.DoesNotContain("Kết quả", content.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Welcome_help_explains_owner_and_admin_access()
    {
        var options = new MonzeCommandOptions("*", MonzeCommandNames.Monze);

        var memberHelp = MonzeHelpCatalog.ForModule(
            MonzeCommandNames.Welcome,
            options,
            isAdmin: false,
            canManageWelcome: false);
        Assert.Contains("owner hoặc admin", string.Join(" ", memberHelp.Select(static entry => entry.Value)), StringComparison.Ordinal);

        var adminHelp = MonzeHelpCatalog.ForModule(
            MonzeCommandNames.Welcome,
            options,
            isAdmin: true,
            canManageWelcome: true);
        var adminText = string.Join("\n", adminHelp.Select(static entry => entry.Name + " " + entry.Value));
        Assert.Contains("*welcome setup", adminText, StringComparison.Ordinal);
        Assert.Contains("*welcome setup remove", adminText, StringComparison.Ordinal);
    }

    [Fact]
    public void Welcome_settings_use_radio_inputs_and_embed_fields()
    {
        var content = MonzeMessageBuilder.WelcomeSettings(new WelcomeSettings(
            true,
            "Xin chào",
            4,
            new WelcomeEmbedSettings(Description: "Nội dung")));

        var raw = content.RawJson;
        Assert.Contains("\"type\":5", raw, StringComparison.Ordinal);
        Assert.Contains(MonzeButtonId.WelcomeStatus, raw, StringComparison.Ordinal);
        Assert.Contains(MonzeButtonId.WelcomePreview, raw, StringComparison.Ordinal);
        Assert.Contains(MonzeButtonId.WelcomeSave, raw, StringComparison.Ordinal);
        Assert.Contains(MonzeButtonId.WelcomeCancel, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Kết quả", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_interactive_payloads_keep_loading_avatar_and_agent_messages_public()
    {
        var loading = MonzeMessageBuilder.AiLoading();
        var avatar = MonzeMessageBuilder.Avatar(new MonzeAvatarTarget(
            7,
            "Người dùng",
            "https://cdn.example/avatar.png"));
        var waiting = MonzeMessageBuilder.AgentWaiting();
        var summarizing = MonzeMessageBuilder.AgentSummarizing();
        var summary = MonzeMessageBuilder.MeetingSummary("Nội dung cuộc họp.");

        Assert.Contains("\"type\":6", loading.RawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":1", loading.RawJson, StringComparison.Ordinal);
        Assert.Null(avatar.Components);
        Assert.Null(waiting.Components);
        Assert.Null(summarizing.Components);
        Assert.Null(summary.Components);
    }

    [Fact]
    public void Interactive_help_and_setup_payloads_keep_controls()
    {
        var options = new MonzeCommandOptions("*", MonzeCommandNames.Monze);
        var help = MonzeMessageBuilder.HelpPage("commands", options, isOwner: true);
        var setup = MonzeMessageBuilder.WelcomeSettings(null);

        Assert.NotNull(help.Components);
        Assert.NotNull(setup.Components);
        Assert.Contains("\"type\":1", help.RawJson, StringComparison.Ordinal);
        Assert.Contains("\"type\":5", setup.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Welcome_preview_carries_the_server_draft_token_on_save_button()
    {
        const string token = "ABC123";
        var content = MonzeMessageBuilder.WelcomePreview(
            new WelcomeEmbedSettings(Title: "Mẫu"),
            new MonzeCommandOptions("*", MonzeCommandNames.Monze),
            token);

        var raw = content.RawJson;
        Assert.Contains(MonzeButtonId.WelcomeSaveFor(token), raw, StringComparison.Ordinal);
        Assert.True(MonzeButtonId.TryReadWelcomeSaveToken(
            MonzeButtonId.WelcomeSaveFor(token),
            out var parsed));
        Assert.Equal(token, parsed);
    }

    [Fact]
    public void Welcome_setup_explains_that_submit_saves_the_draft()
    {
        var raw = MonzeMessages.WelcomeSetupText();

        Assert.Contains("Preview để xem trước, Submit để lưu.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("apply", raw, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadColor(string raw)
        => Parse(raw).GetProperty("embed")[0].GetProperty("color").GetString()!;

    private static JsonElement Parse(string raw)
        => JsonDocument.Parse(raw).RootElement.Clone();

}
