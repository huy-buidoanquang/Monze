using Monze.Application.Commands;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Property.Ui;

/// <summary>
/// G08: MonzeHelpCatalog, exhaustively over permissions (admin, welcome
/// manager, owner) × command prefix and root × every topic and alias. Help
/// never advertises an action the reader may not perform, every entry uses
/// the configured prefix, the main menu lists setup only for the owner, and
/// no entry contains a raw id.
/// </summary>
public sealed class G08HelpCatalogProperties
{
    private static readonly MonzeCommandOptions[] Options =
    [
        MonzeCommandOptions.Default,
        new("!", null),
        new("*", "bot"),
        new("monze ", null),
        new(".", "monze")
    ];

    private static readonly string[] Topics =
    [
        "", "commands", "monze", "welcome", "WELCOME", "meeting", "summary", "setup", "role", "ai", "translate",
        "avatar", "ava", "AVT", "help", "unknown", "xin chào", "🎯"
    ];

    [Fact]
    [Req("REQ-HELP-001")]
    [Covers("cmd:help")]
    public void Help_never_advertises_actions_the_reader_cannot_perform()
    {
        using var ledger = CaseLedger.Open("property", "G08")
            .Dimension("admin", "yes", "no")
            .Dimension("welcome", "yes", "no")
            .Dimension("owner", "yes", "no")
            .Dimension("options", Options.Select(static (_, i) => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray())
            .Dimension("topic", Topics);
        var failures = new List<string>();
        foreach (var isAdmin in new[] { true, false })
        {
            foreach (var canManageWelcome in new[] { true, false })
            {
                foreach (var isOwner in new[] { true, false })
                {
                    for (var o = 0; o < Options.Length; o++)
                    {
                        var options = Options[o];
                        var menu = MonzeHelpCatalog.Commands(options, isAdmin, canManageWelcome, isOwner);
                        var menuSetup = menu.Any(static entry => entry.Name.Contains(MonzeCommandNames.Setup, StringComparison.Ordinal));
                        foreach (var topic in Topics)
                        {
                            var tags = new Dictionary<string, string>
                            {
                                ["admin"] = isAdmin ? "yes" : "no",
                                ["welcome"] = canManageWelcome ? "yes" : "no",
                                ["owner"] = isOwner ? "yes" : "no",
                                ["options"] = o.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["topic"] = topic
                            };
                            var input = $"admin={isAdmin} welcome={canManageWelcome} owner={isOwner} prefix='{options.Prefix}' root='{options.Root}' topic='{topic}'";
                            var entries = MonzeHelpCatalog.ForModule(topic, options, isAdmin, canManageWelcome, isOwner);
                            var problem = Check(entries, options, topic, isAdmin, canManageWelcome, isOwner)
                                ?? (menuSetup != isOwner ? "main menu setup entry does not follow ownership" : null)
                                ?? (menu.Count != 6 + (isOwner ? 1 : 0) ? $"main menu has {menu.Count} entries" : null);
                            if (problem is null)
                            {
                                ledger.Pass(input, tags);
                            }
                            else
                            {
                                ledger.Fail(input, problem, tags);
                                failures.Add($"{input}: {problem}");
                            }
                        }
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
        var (required, covered, missing) = ledger.PairwiseCoverage();
        Assert.True(covered == required, $"pairwise {covered}/{required}: {string.Join(", ", missing)}");
    }

    private static string? Check(
        IReadOnlyList<MonzeHelpEntry> entries,
        MonzeCommandOptions options,
        string topic,
        bool isAdmin,
        bool canManageWelcome,
        bool isOwner)
    {
        if (entries.Count == 0)
        {
            return "no entries";
        }

        foreach (var entry in entries)
        {
            if (entry.Name.Length > 0 && !entry.Name.StartsWith(options.Prefix, StringComparison.Ordinal))
            {
                return $"'{entry.Name}' does not start with the prefix";
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(entry.Name + entry.Value, "[0-9]{15,}"))
            {
                return "raw id in help";
            }
        }

        var commands = string.Join("\n", entries.Select(static entry => entry.Name));
        var texts = string.Join("\n", entries.Select(static entry => entry.Value));
        return MonzeCommandNames.Normalize(topic) switch
        {
            MonzeCommandNames.Setup when !isOwner => entries is [{ Name: "" } only] && only.Value == MonzeMessages.OwnerOnly ? null : "setup actions shown to a non-owner",
            MonzeCommandNames.Setup => commands.Contains("admin add", StringComparison.Ordinal) ? null : "owner does not see setup actions",
            MonzeCommandNames.Role when !isAdmin => entries is [{ Name: "" } only] && only.Value == MonzeMessages.AdminOnly ? null : "role actions shown to a non-admin",
            MonzeCommandNames.Summary when !isAdmin => entries is [{ Name: "" } only] && only.Value == MonzeMessages.SummaryAdminOnly ? null : "summary lookup shown to a non-admin",
            MonzeCommandNames.Welcome when !canManageWelcome => entries is [{ Name: "" } only] && only.Value == MonzeMessages.WelcomeAdminOnly ? null : "welcome actions shown without permission",
            MonzeCommandNames.Welcome => texts.Contains("{user}", StringComparison.Ordinal) ? null : "welcome placeholders are not documented",
            _ => null
        };
    }
}
