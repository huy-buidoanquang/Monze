using System.Text.Json;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Ui;
using Xunit;

namespace Monze.Tests;

public sealed class AvatarFeatureTests
{
    [Fact]
    public void Avatar_aliases_normalize_to_the_same_command()
    {
        Assert.Equal(MonzeCommandNames.Avatar, MonzeCommandNames.Normalize("avatar"));
        Assert.Equal(MonzeCommandNames.Avatar, MonzeCommandNames.Normalize("ava"));
        Assert.Equal(MonzeCommandNames.Avatar, MonzeCommandNames.Normalize("avt"));
    }

    [Fact]
    public void Profile_label_prefers_clan_nick_then_display_name_then_username()
    {
        Assert.Equal("Clan Nick", new UserProfileSnapshot(
            1, 2, "Clan Nick", "Display", "username", null, DateTimeOffset.UtcNow).Label);
        Assert.Equal("Display", new UserProfileSnapshot(
            1, 2, null, "Display", "username", null, DateTimeOffset.UtcNow).Label);
        Assert.Equal("username", new UserProfileSnapshot(
            1, 2, null, null, "username", null, DateTimeOffset.UtcNow).Label);
    }

    [Fact]
    public void Avatar_card_uses_display_label_and_image_without_raw_ids()
    {
        var raw = MonzeMessageBuilder.Avatar(new MonzeAvatarTarget(
            2104288434238525440,
            "Clan Nick",
            "https://cdn.example/avatar.png")).RawJson;
        using var json = JsonDocument.Parse(raw);
        var embed = json.RootElement.GetProperty("embed")[0];

        Assert.Equal("Avatar", embed.GetProperty("title").GetString());
        Assert.Contains("Clan Nick", embed.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("https://cdn.example/avatar.png", embed.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("2104288434238525440", embed.GetRawText(), StringComparison.Ordinal);
    }
}
