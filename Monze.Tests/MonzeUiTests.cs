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
        Assert.Contains("*monze points", member.GetRawText(), StringComparison.Ordinal);

        var admin = Parse(MonzeMessageBuilder.HelpPage("commands", options, isAdmin: true).RawJson);
        Assert.Contains("*monze welcome on|off", admin.GetRawText(), StringComparison.Ordinal);
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
        Assert.DoesNotContain("Kết quả", raw, StringComparison.Ordinal);
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

    private static string ReadColor(string raw)
        => Parse(raw).GetProperty("embed")[0].GetProperty("color").GetString()!;

    private static JsonElement Parse(string raw)
        => JsonDocument.Parse(raw).RootElement.Clone();

}
