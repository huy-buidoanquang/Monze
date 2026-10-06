using Monze.Application.Commands;
using Monze.Application;
using Monze.Domain;
using Xunit;

namespace Monze.Tests;

public sealed class BrowserRegressionTests
{
    [Theory]
    [InlineData("Daily", "Standup", "daily", MeetingScheduleKind.Daily)]
    [InlineData("Weekly", "Sync", "weekly", MeetingScheduleKind.Weekly)]
    [InlineData("Once", "Review", "once", MeetingScheduleKind.Once)]
    public void Named_schedule_preserves_frequency_word_in_title_and_excludes_kind(
        string first, string second, string kind, MeetingScheduleKind expected)
    {
        Assert.True(MeetingCommandParser.TryParse(
            [first, second, kind, "12/10/2026", "10:00"], out var request));

        Assert.Equal(first + " " + second, request!.Name);
        Assert.Equal(expected, request.Kind);
        Assert.Equal("12/10/2026 10:00", request.WhenText);
    }

    [Fact]
    public void Named_weekly_schedule_does_not_include_kind_in_title()
    {
        Assert.True(MeetingCommandParser.TryParse(
            ["Sync", "weekly", "12/10/2026", "10:00"], out var request));

        Assert.Equal("Sync", request!.Name);
        Assert.Equal(MeetingScheduleKind.Weekly, request.Kind);
    }

    [Theory]
    [InlineData("summary", true)]
    [InlineData("translate", true)]
    [InlineData("composer", true)]
    [InlineData("simplify", true)]
    [InlineData("TRANSLATE", true)]
    [InlineData("help", false)]
    [InlineData("foo", false)]
    public void Only_supported_ai_operations_receive_public_loading_message(string operation, bool expected)
    {
        Assert.Equal(expected, MonzeBot.IsAiCommand(new CommandArguments(["ai", operation, "input"])));
    }

    [Fact]
    public async Task Command_welcome_preview_uses_its_snapshot_and_rejects_newer_configuration()
    {
        var dependencies = new MonzeAppTestDependencies
        {
            IsAdmin = true,
            WelcomeSettings = new WelcomeSettings(true, "current text", 10,
                new WelcomeEmbedSettings(Title: "current title"))
        };
        var app = dependencies.CreateApp();
        var preview = await app.HandleMonzeAsync(1, 2, 3,
            ["welcome", "preview"], CancellationToken.None);
        Assert.NotNull(preview.WelcomeDraftToken);
        Assert.Equal("current title", preview.WelcomeDraft!.Title);

        dependencies.WelcomeSettings = new WelcomeSettings(false, "newer text", 11);
        var outcome = await app.SaveWelcomeDraftAsync(1, 2, 3,
            preview.WelcomeDraftToken!, CancellationToken.None);

        Assert.Equal(MonzeMessages.WelcomeConfigurationChanged, outcome.Text);
        Assert.Equal(MonzeTone.Warn, outcome.Tone);
        Assert.Equal(0, dependencies.WelcomeConfigurationWrites);
    }

    [Fact]
    public async Task Command_welcome_preview_saves_when_its_version_is_current()
    {
        var dependencies = new MonzeAppTestDependencies
        {
            IsAdmin = true,
            WelcomeSettings = new WelcomeSettings(true, "current text", 10,
                new WelcomeEmbedSettings(Title: "current title"))
        };
        var app = dependencies.CreateApp();
        var preview = await app.HandleMonzeAsync(1, 2, 3,
            ["welcome", "preview"], CancellationToken.None);
        var outcome = await app.SaveWelcomeDraftAsync(1, 2, 3,
            preview.WelcomeDraftToken!, CancellationToken.None);

        Assert.Equal(MonzeMessages.WelcomeEmbedSaved, outcome.Text);
        Assert.Equal(1, dependencies.WelcomeConfigurationWrites);
        Assert.Equal("current text", dependencies.WelcomeText);
    }
}
