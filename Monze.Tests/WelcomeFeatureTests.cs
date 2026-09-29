using Mezon.Net.Models;
using Monze.Application;
using Monze.Ui;
using Xunit;

namespace Monze.Tests;

public sealed class WelcomeFeatureTests
{
    [Fact]
    public void Setup_exposes_grouped_fields_and_actions()
    {
        var raw = MonzeMessageBuilder.WelcomeSettings(
            new WelcomeSettings(true, "Chào {user}", 1),
            WelcomeSetupSection.Advanced).RawJson;

        Assert.Contains(MonzeButtonId.WelcomeGeneral, raw, StringComparison.Ordinal);
        Assert.Contains(MonzeButtonId.WelcomeImages, raw, StringComparison.Ordinal);
        Assert.Contains(MonzeButtonId.WelcomeAuthorSection, raw, StringComparison.Ordinal);
        Assert.Contains(MonzeButtonId.WelcomeAdvanced, raw, StringComparison.Ordinal);
        Assert.Contains("Submit", raw, StringComparison.Ordinal);
        Assert.Contains(MonzeButtonId.WelcomeFieldName, raw, StringComparison.Ordinal);
        Assert.Contains(MonzeButtonId.WelcomeFieldValue, raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Welcome_template_renders_member_role_and_channel_metadata()
    {
        var settings = new WelcomeSettings(
            true,
            "Chào {user}, xem {channel:general} và nhận {role:Member}.",
            1,
            new WelcomeEmbedSettings(Title: "Xin chào"));
        var result = WelcomeMessageRenderer.Render(
            settings,
            42,
            "Alice",
            new Dictionary<string, (long Id, string Label)>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = (42, "Alice")
            },
            new Dictionary<string, (long Id, string Label)>(StringComparer.OrdinalIgnoreCase)
            {
                ["Member"] = (7, "Member")
            },
            new Dictionary<string, (long Id, string Label)>(StringComparer.OrdinalIgnoreCase)
            {
                ["general"] = (8, "general")
            });

        Assert.Equal("Chào @Alice, xem #general và nhận @Member.", result.Content.Text);
        Assert.Equal(2, result.Mentions.Count);
        Assert.Equal(42, result.Mentions[0].UserId);
        Assert.Equal(7, result.Mentions[1].RoleId);
        Assert.Single(result.Content.Hashtags!);
        Assert.Equal("8", result.Content.Hashtags![0].ChannelId);
        Assert.DoesNotContain("210", result.Content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_welcome_template_target_is_kept_as_safe_plain_text()
    {
        var result = WelcomeMessageRenderer.Render(
            new WelcomeSettings(true, "{role:Missing} {channel:missing}", 1),
            42,
            "Alice",
            new Dictionary<string, (long Id, string Label)>(),
            new Dictionary<string, (long Id, string Label)>(),
            new Dictionary<string, (long Id, string Label)>());

        Assert.Equal("{role:Missing} {channel:missing}", result.Content.Text);
        Assert.Empty(result.Mentions);
        Assert.True(result.Content.Hashtags is null || result.Content.Hashtags.Count == 0);
    }
}
