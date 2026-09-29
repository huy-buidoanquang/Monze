using Monze.Application.Commands;
using Xunit;

namespace Monze.Tests;

public sealed class CommandHelpTests
{
    [Fact]
    public void RootHelpCatalog_contains_only_module_commands()
    {
        var options = new MonzeCommandOptions("*", MonzeCommandNames.Monze);
        var text = Text(MonzeHelpCatalog.Commands(options, isAdmin: false, canManageWelcome: false));

        Assert.Contains("*meeting", text, StringComparison.Ordinal);
        Assert.Contains("*summary", text, StringComparison.Ordinal);
        Assert.Contains("*welcome", text, StringComparison.Ordinal);
        Assert.Contains("*role", text, StringComparison.Ordinal);
        Assert.Contains("*ai", text, StringComparison.Ordinal);
        Assert.DoesNotContain("*monze", text, StringComparison.Ordinal);
        Assert.DoesNotContain("points", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("event", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RootHelp_adds_setup_only_for_owner()
    {
        var options = new MonzeCommandOptions("*", MonzeCommandNames.Monze);
        var owner = Text(MonzeHelpCatalog.Commands(options, true, true, isOwner: true));
        var admin = Text(MonzeHelpCatalog.Commands(options, true, true, isOwner: false));

        Assert.Contains("*setup", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("*setup", admin, StringComparison.Ordinal);
    }

    [Fact]
    public void ModuleHelp_uses_direct_commands_and_new_welcome_role_ai_shapes()
    {
        var options = new MonzeCommandOptions("*", MonzeCommandNames.Monze);
        var welcome = Text(MonzeHelpCatalog.ForModule(MonzeCommandNames.Welcome, options, true, true));
        var role = Text(MonzeHelpCatalog.ForModule(MonzeCommandNames.Role, options, true, false));
        var ai = Text(MonzeHelpCatalog.ForModule(MonzeCommandNames.Ai, options, false, false));

        Assert.Contains("*welcome message remove", welcome, StringComparison.Ordinal);
        Assert.DoesNotContain("setting", welcome, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("*setup welcome", welcome, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*role on|off", role, StringComparison.Ordinal);
        Assert.Contains("*role join <tên_role>", role, StringComparison.Ordinal);
        Assert.Contains("*role tenure <tên_role>", role, StringComparison.Ordinal);
        Assert.DoesNotContain("self", role, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*ai summary <nội dung>", ai, StringComparison.Ordinal);
        Assert.Contains("*ai translate <nội dung>", ai, StringComparison.Ordinal);
        Assert.Contains("*ai composer <nội dung>", ai, StringComparison.Ordinal);
        Assert.Contains("*ai simplify <nội dung>", ai, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryHelp_is_restricted_to_admin()
    {
        var options = new MonzeCommandOptions("*", MonzeCommandNames.Monze);
        var member = Text(MonzeHelpCatalog.ForModule(MonzeCommandNames.Summary, options, false, false));
        var admin = Text(MonzeHelpCatalog.ForModule(MonzeCommandNames.Summary, options, true, false));

        Assert.Contains("Chỉ owner hoặc admin", member, StringComparison.Ordinal);
        Assert.Contains("*summary <id>", admin, StringComparison.Ordinal);
    }

    [Fact]
    public void Rootless_options_still_render_direct_commands()
    {
        var options = new MonzeCommandOptions("*", null);
        var text = Text(MonzeHelpCatalog.ForModule(MonzeCommandNames.Meeting, options, false, false));

        Assert.Contains("*meeting now", text, StringComparison.Ordinal);
        Assert.DoesNotContain("*monze meeting", text, StringComparison.Ordinal);
        Assert.DoesNotContain(MonzeCommandNames.Help, MonzeCommandNames.DirectModules);
    }

    private static string Text(IReadOnlyList<MonzeHelpEntry> entries)
        => string.Join("\n", entries.Select(static entry => entry.Name + " " + entry.Value));
}
