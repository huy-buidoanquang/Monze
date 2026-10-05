using Mezon.Net.Client;
using Mezon.Net.Sdk.Builders;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;

namespace Monze.Ui;

public static partial class MonzeMessageBuilder
{
    public static MessageContent Card(CommandOutcome outcome)
        => Card(outcome, MonzeCommandOptions.Default);

    public static MessageContent Card(CommandOutcome outcome, MonzeCommandOptions options)
    {
        if (outcome.HelpTopic is not null)
        {
            return HelpPage(
                outcome.HelpTopic,
                options,
                outcome.HelpForAdmin,
                outcome.CanManageWelcome,
                outcome.HelpForOwner);
        }

        if (outcome.ShowWelcomeSettings)
        {
            return WelcomeSettings(outcome.WelcomeSettings);
        }

        if (outcome.ShowWelcomePreview && outcome.WelcomeDraft is not null)
        {
            return WelcomePreview(
                outcome.WelcomeDraft,
                options,
                outcome.WelcomeDraftToken,
                outcome.WelcomeMessageText);
        }

        if (outcome.ShowMeetingSchedules && outcome.MeetingSchedules is not null)
        {
            return MeetingSchedules(outcome.MeetingSchedules, options);
        }

        var fields = ToEmbedFields(outcome);

        if (outcome.ShowWelcomeHelp)
        {
            return Build(
                outcome.Title,
                MonzeEmbedColors.For(outcome.Tone),
                fields,
                WelcomeHelpButton());
        }

        if (outcome.ShowHelpButtons)
        {
            return BuildWithRows(
                outcome.Title,
                MonzeEmbedColors.For(outcome.Tone),
                fields,
                HelpNavigationRows(outcome.HelpForOwner));
        }

        return Build(outcome.Title, MonzeEmbedColors.For(outcome.Tone), fields, components: null);
    }
    public static MessageContent Card(string title, string body, MonzeTone tone)
        => Build(
            title,
            MonzeEmbedColors.For(tone),
            [new MessageEmbedField(string.Empty, body)],
            components: null);

    public static MessageContent AiLoading()
        => BuildWithRows(
            "AI",
            MonzeEmbedColors.For(MonzeTone.Info),
            [new MessageEmbedField(string.Empty, "Đang xử lý yêu cầu…")],
            [new ButtonBuilder()
                .AddAnimation(
                    "monze_ai_loading",
                    pool: ["⏳", "⌛", "⏳"],
                    repeat: 0,
                    duration: 900)
                .BuildComponents()]);
    public static MessageContent Avatar(MonzeAvatarTarget target)
    {
        var embed = new MessageEmbedBuilder()
            .SetTitle("Avatar")
            .SetColor(MonzeEmbedColors.For(MonzeTone.Info))
            .AddField("Người dùng", target.Label);

        if (!string.IsNullOrWhiteSpace(target.AvatarUrl))
        {
            embed.SetImage(target.AvatarUrl);
        }

        return new MessageContentBuilder()
            .AddEmbed(embed.Build())
            .Build();
    }
    private static IReadOnlyList<MessageEmbedField> ToEmbedFields(CommandOutcome outcome)
    {
        if (outcome.Fields is not { Count: > 0 })
        {
            return [new MessageEmbedField(string.Empty, outcome.Text)];
        }

        var count = Math.Min(outcome.Fields.Count, 25);
        var fields = new List<MessageEmbedField>(count);
        for (var i = 0; i < count; i++)
        {
            var field = outcome.Fields[i];
            fields.Add(new MessageEmbedField(field.Name, field.Value, inline: field.Inline));
        }

        return fields;
    }
    private static MessageContent Build(
        string title,
        string color,
        IReadOnlyList<MessageEmbedField> fields,
        IReadOnlyList<MessageComponent>? components)
    {
        var embedBuilder = new MessageEmbedBuilder()
            .SetColor(color)
            .SetTitle(title);

        foreach (var field in fields)
        {
            embedBuilder.AddFieldValue(field);
        }

        var contentBuilder = new MessageContentBuilder().AddEmbed(embedBuilder.Build());
        if (components is not null)
        {
            contentBuilder.AddActionRow(components);
        }

        return contentBuilder.Build();
    }

    private static MessageContent BuildWithRows(
        string title,
        string color,
        IReadOnlyList<MessageEmbedField> fields,
        IReadOnlyList<IReadOnlyList<MessageComponent>> rows)
    {
        var embedBuilder = new MessageEmbedBuilder()
            .SetColor(color)
            .SetTitle(title);
        foreach (var field in fields)
        {
            embedBuilder.AddFieldValue(field);
        }

        var contentBuilder = new MessageContentBuilder().AddEmbed(embedBuilder.Build());
        for (var i = 0; i < rows.Count; i++)
        {
            contentBuilder.AddActionRow(rows[i]);
        }

        return contentBuilder.Build();
    }
}
