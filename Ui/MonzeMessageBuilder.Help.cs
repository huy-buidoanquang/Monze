using Mezon.Net.Client;
using Mezon.Net.Sdk.Builders;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;

namespace Monze.Ui;

public static partial class MonzeMessageBuilder
{
    public static MessageContent HelpPage(
        string page,
        MonzeCommandOptions options,
        bool isAdmin = true,
        bool canManageWelcome = false,
        bool isOwner = false)
        => page.ToLowerInvariant() switch
        {
            "" or "commands" or "monze" => BuildCommandsHelp(options, isAdmin, canManageWelcome, isOwner),
            "meeting" => BuildMeetingHelp(options, isOwner),
            _ => BuildModuleHelp(page, options, isAdmin, canManageWelcome, isOwner)
        };

    private static MessageContent BuildCommandsHelp(
        MonzeCommandOptions options,
        bool isAdmin,
        bool canManageWelcome,
        bool isOwner)
    {
        var entries = MonzeHelpCatalog.Commands(options, isAdmin, canManageWelcome, isOwner);
        var fields = new List<MessageEmbedField>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            fields.Add(new MessageEmbedField(entries[i].Name, entries[i].Value));
        }

        return BuildWithRows(
            MonzeMessages.TitleHelp,
            MonzeEmbedColors.For(MonzeTone.Info),
            fields,
            HelpNavigationRows(isOwner));
    }

    private static MessageContent BuildMeetingHelp(MonzeCommandOptions options, bool isOwner)
        => BuildHelpEntries(
            MonzeHelpCatalog.ForModule(MonzeCommandNames.Meeting, options, false, false, isOwner),
            HelpNavigationRows(isOwner));

    private static MessageContent BuildModuleHelp(
        string page,
        MonzeCommandOptions options,
        bool isAdmin,
        bool canManageWelcome,
        bool isOwner)
        => BuildHelpEntries(
            MonzeHelpCatalog.ForModule(
                page,
                options,
                isAdmin,
                page.Equals(MonzeCommandNames.Welcome, StringComparison.OrdinalIgnoreCase)
                    ? canManageWelcome
                    : isAdmin,
                isOwner),
            HelpNavigationRows(isOwner));

    private static MessageContent BuildHelpEntries(
        IReadOnlyList<MonzeHelpEntry> entries,
        IReadOnlyList<IReadOnlyList<MessageComponent>> rows)
    {
        var fields = new List<MessageEmbedField>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            fields.Add(new MessageEmbedField(entries[i].Name, entries[i].Value));
        }

        return BuildWithRows(
            MonzeMessages.TitleHelp,
            MonzeEmbedColors.For(MonzeTone.Info),
            fields,
            rows);
    }

    private static IReadOnlyList<IReadOnlyList<MessageComponent>> HelpNavigationRows(bool isOwner)
    {
        var rows = new List<IReadOnlyList<MessageComponent>>
        {
            new ButtonBuilder()
                .AddButton(MonzeButtonId.HelpMeeting, "Meeting", style: (int)MessageButtonStyle.Success)
                .AddButton(MonzeButtonId.HelpSummary, "Summary", style: (int)MessageButtonStyle.Secondary)
                .AddButton(MonzeButtonId.HelpWelcome, "Welcome", style: (int)MessageButtonStyle.Secondary)
                .AddButton(MonzeButtonId.HelpRole, "Role", style: (int)MessageButtonStyle.Secondary)
                .AddButton(MonzeButtonId.HelpAi, "AI", style: (int)MessageButtonStyle.Primary)
                .BuildComponents()
        };

        var secondary = new ButtonBuilder();

        if (isOwner)
        {
            secondary.AddButton(MonzeButtonId.HelpSetup, "Setup", style: (int)MessageButtonStyle.Secondary);
        }

        secondary.AddButton(MonzeButtonId.HelpClose, "Đóng", style: (int)MessageButtonStyle.Danger);
        rows.Add(secondary.BuildComponents());
        return rows;
    }
}
