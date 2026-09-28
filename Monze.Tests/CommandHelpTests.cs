using Monze.Application.Commands;
using Xunit;

namespace Monze.Tests;

public sealed class CommandHelpTests
{
    [Fact]
    public void CommandHelp_RendersRootedCommandAndExample()
    {
        var text = MonzeMessages.CommandHelp(
            MonzeCommandNames.Points,
            new MonzeCommandOptions("*", MonzeCommandNames.Monze));

        Assert.Contains("*monze points", text, StringComparison.Ordinal);
        Assert.Contains("Ví dụ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StandaloneHelp_DoesNotAddMonzeRoot()
    {
        var options = new MonzeCommandOptions("*", null);

        Assert.Contains("*meeting now", MonzeMessages.MeetingHelp(options), StringComparison.Ordinal);
        Assert.DoesNotContain("*monze meeting", MonzeMessages.MeetingHelp(options), StringComparison.Ordinal);
        Assert.Equal("*points", options.DirectCommand(MonzeCommandNames.Points));
    }

    [Fact]
    public void ModuleHelp_ContainsUsableExamples()
    {
        var rooted = new MonzeCommandOptions("*", MonzeCommandNames.Monze);
        var standalone = new MonzeCommandOptions("*", null);

        Assert.Contains("*monze event create Town hall", MonzeMessages.EventHelp(rooted), StringComparison.Ordinal);
        Assert.Contains("*monze faq add", MonzeMessages.FaqHelp(rooted), StringComparison.Ordinal);
        Assert.Contains("*monze role self Gamer", MonzeMessages.RoleHelp(rooted), StringComparison.Ordinal);
        Assert.Contains("*monze role rule tenure Trusted | 30", MonzeMessages.RoleHelp(rooted), StringComparison.Ordinal);
        Assert.Contains("*monze role rule existing", MonzeMessages.RoleHelp(rooted), StringComparison.Ordinal);
        Assert.Contains("*meeting daily 09:00", MonzeMessages.MeetingHelp(standalone), StringComparison.Ordinal);
        Assert.Contains("*monze topic add", MonzeMessages.TopicHelp(rooted), StringComparison.Ordinal);
    }

    [Fact]
    public void SetupHelp_ExplainsWelcomeAndDelegateCommands()
    {
        var text = MonzeMessages.SetupHelp(
            new MonzeCommandOptions("*", MonzeCommandNames.Monze));

        Assert.Contains("*monze setup welcome on", text, StringComparison.Ordinal);
        Assert.Contains("*monze setup admin add", text, StringComparison.Ordinal);
        Assert.Contains("<@user>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<userId>", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WelcomeHelpSupportsStandaloneRootlessCommand()
    {
        var options = new MonzeCommandOptions("*", null);

        var text = MonzeMessages.CommandHelp(MonzeCommandNames.Welcome, options);

        Assert.Contains("*welcome on", text, StringComparison.Ordinal);
        Assert.Contains("Ví dụ", text, StringComparison.Ordinal);
        Assert.Contains(MonzeCommandNames.Welcome, MonzeCommandNames.DirectModules);
        Assert.DoesNotContain(MonzeCommandNames.Help, MonzeCommandNames.DirectModules);
    }
}
