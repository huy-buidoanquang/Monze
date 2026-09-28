using Mezon.Net.Client;
using Mezon.Net.Sdk.Builders;
using Monze.Application;
using Monze.Application.Commands;

namespace Monze.Ui;

public static class MonzeMessageBuilder
{
    public static MessageContent Card(CommandOutcome outcome)
        => Card(outcome, MonzeCommandOptions.Default);

    public static MessageContent Card(CommandOutcome outcome, MonzeCommandOptions options)
    {
        if (outcome.HelpTopic is not null)
        {
            return HelpPage(outcome.HelpTopic, options, outcome.HelpForAdmin);
        }

        if (outcome.ShowWelcomeSettings)
        {
            return WelcomeSettings(outcome.WelcomeSettings);
        }

        if (outcome.ShowWelcomePreview && outcome.WelcomeDraft is not null)
        {
            return WelcomePreview(outcome.WelcomeDraft, options, outcome.WelcomeDraftToken);
        }

        var fields = new List<MessageEmbedField>(1)
        {
            new(string.Empty, outcome.Text)
        };

        IReadOnlyList<MessageComponent>? components = null;
        if (outcome.ShowWelcomeHelp)
        {
            components = WelcomeHelpButton();
        }
        else if (outcome.ShowHelpButtons)
        {
            components = HelpButtons();
        }

        return Build(outcome.Title, MonzeEmbedColors.For(outcome.Tone), fields, components);
    }

    public static MessageContent Card(string title, string body, MonzeTone tone)
        => Build(
            title,
            MonzeEmbedColors.For(tone),
            [new MessageEmbedField(string.Empty, body)],
            components: null);

    public static MessageContent HelpPage(
        string page,
        MonzeCommandOptions options,
        bool isAdmin = true)
        => page.ToLowerInvariant() switch
        {
            "" or "commands" or "monze" => BuildCommandsHelp(options, isAdmin),
            "event" => BuildEventHelp(options, isAdmin),
            "meeting" => BuildMeetingHelp(options),
            _ => BuildModuleHelp(page, options, isAdmin)
        };

    public static MessageContent WelcomeCard(WelcomeSettings settings)
    {
        var draft = settings.Embed ?? new WelcomeEmbedSettings(
            Title: "Chào mừng",
            Description: settings.Text ?? MonzeMessages.DefaultWelcomeText);
        return BuildEmbedMessage(draft, components: null, saveInstruction: null);
    }

    public static MessageContent WelcomeSettings(WelcomeSettings? settings)
    {
        var current = settings ?? new WelcomeSettings(false, null, 0);
        var draft = current.Embed ?? new WelcomeEmbedSettings(
            Title: "Chào mừng",
            Description: current.Text ?? MonzeMessages.DefaultWelcomeText);
        var fields = new List<MessageEmbedField>
        {
            new(
                "Trạng thái",
                "Chọn một trạng thái rồi bấm Lưu.",
                input: new RadioMessageComponent(
                    MonzeButtonId.WelcomeStatus,
                    [
                        new MessageRadioOption("Bật", "on", Name: MonzeButtonId.WelcomeStatus),
                        new MessageRadioOption("Tắt", "off", Name: MonzeButtonId.WelcomeStatus)
                    ],
                    maxOptions: 1)),
            InputField("Tiêu đề", "Tiêu đề embed", MonzeButtonId.WelcomeTitle, draft.Title),
            InputField("Nội dung", "Nội dung chính", MonzeButtonId.WelcomeDescription, draft.Description, textarea: true),
            InputField("Liên kết", "Chỉ nhận URL https://", MonzeButtonId.WelcomeUrl, draft.Url),
            InputField("Màu", "Mã hex, ví dụ #5865F2", MonzeButtonId.WelcomeColor, draft.Color),
            InputField("Tác giả", "Tên tác giả", MonzeButtonId.WelcomeAuthor, draft.AuthorName),
            InputField("Icon tác giả", "URL https://", MonzeButtonId.WelcomeAuthorIcon, draft.AuthorIconUrl),
            InputField("Liên kết tác giả", "URL https://", MonzeButtonId.WelcomeAuthorUrl, draft.AuthorUrl),
            InputField("Ảnh nhỏ", "Thumbnail URL https://", MonzeButtonId.WelcomeThumbnail, draft.ThumbnailUrl),
            InputField("Ảnh lớn", "Image URL https://", MonzeButtonId.WelcomeImage, draft.ImageUrl),
            InputField("Chân trang", "Nội dung footer", MonzeButtonId.WelcomeFooter, draft.FooterText),
            InputField("Icon chân trang", "URL https://", MonzeButtonId.WelcomeFooterIcon, draft.FooterIconUrl),
            InputField("Tên field", "Tên field phụ", MonzeButtonId.WelcomeFieldName, draft.FieldName),
            InputField("Giá trị field", "Nội dung field phụ", MonzeButtonId.WelcomeFieldValue, draft.FieldValue, textarea: true)
        };

        var buttons = new ButtonBuilder()
            .AddButton(MonzeButtonId.WelcomePreview, "Xem trước", style: (int)MessageButtonStyle.Primary)
            .AddButton(MonzeButtonId.WelcomeSave, "Lưu", style: (int)MessageButtonStyle.Success)
            .BuildComponents();
        return Build(MonzeMessages.TitleWelcomeSetup, MonzeEmbedColors.For(MonzeTone.Info), fields, buttons);
    }

    public static MessageContent WelcomePreview(
        WelcomeEmbedSettings draft,
        MonzeCommandOptions? options = null,
        string? token = null)
    {
        var content = BuildEmbedMessage(
            draft,
            components: null,
            saveInstruction: token is null
                ? null
                : MonzeMessages.WelcomeDraftReady(options ?? MonzeCommandOptions.Default, token));
        var buttons = new ButtonBuilder()
            .AddButton(MonzeButtonId.WelcomeSettings, "Chỉnh sửa", style: (int)MessageButtonStyle.Secondary)
            .AddButton(
                token is null ? MonzeButtonId.WelcomeSave : MonzeButtonId.WelcomeSaveFor(token),
                "Lưu mẫu này",
                style: (int)MessageButtonStyle.Success)
            .BuildComponents();
        return AddComponents(content, buttons);
    }

    private static MessageContent BuildCommandsHelp(MonzeCommandOptions options, bool isAdmin)
    {
        var fields = new List<MessageEmbedField>
        {
            new(
                "Thành viên",
                string.Join(
                    '\n',
                    options.Command(MonzeCommandNames.Points),
                    options.Command(MonzeCommandNames.Leaderboard),
                    options.Command(MonzeCommandNames.Spin),
                    options.Command(MonzeCommandNames.Topic),
                    options.Command(MonzeCommandNames.Info, "<từ khóa>"),
                    options.Command(MonzeCommandNames.Event, MonzeCommandActions.Join),
                    options.DirectCommand(MonzeCommandNames.Meeting, "now"),
                    options.DirectCommand(MonzeCommandNames.Summary))),
            new(
                "AI",
                string.Join(
                    '\n',
                    options.Command(MonzeCommandNames.Summarize, "<nội dung>"),
                    options.Command(MonzeCommandNames.Translate, "<nội dung>"),
                    options.Command(MonzeCommandNames.Rewrite, "<nội dung>"),
                    options.Command(MonzeCommandNames.Shorten, "<nội dung>")))
        };

        if (isAdmin)
        {
            fields.Add(new MessageEmbedField(
                "Quản trị",
                string.Join(
                    '\n',
                    options.Command(MonzeCommandNames.Welcome, "on|off"),
                    options.Command(MonzeCommandNames.Welcome, MonzeCommandActions.Setting),
                    options.Command(MonzeCommandNames.Welcome, MonzeCommandActions.Apply, "<mã>"),
                    options.Command(MonzeCommandNames.Setup, MonzeCommandActions.Admin, "add|remove", "<@user>"),
                    options.Command(MonzeCommandNames.Announce, "<nội dung>"),
                    options.Command(MonzeCommandNames.Faq, MonzeCommandActions.Add, "<câu hỏi>", "|", "<trả lời>"),
                    options.Command(MonzeCommandNames.Event, MonzeCommandActions.Create, "<tên>", "|", "<thời điểm>"),
                    options.Command(MonzeCommandNames.Role, MonzeCommandActions.Allow, "<role>"))));
        }

        fields.Add(new MessageEmbedField(
            string.Empty,
            $"Gõ {options.Command(MonzeCommandNames.Help, "<chức năng>")} để xem ví dụ chi tiết."));
        return Build(MonzeMessages.TitleHelp, MonzeEmbedColors.For(MonzeTone.Info), fields, HelpButtons());
    }

    private static MessageContent BuildEventHelp(MonzeCommandOptions options, bool isAdmin)
        => Build(
            MonzeMessages.TitleHelp,
            MonzeEmbedColors.For(MonzeTone.Info),
            [new MessageEmbedField("Sự kiện", MonzeMessages.EventHelp(options, isAdmin))],
            HelpButtons());

    private static MessageContent BuildMeetingHelp(MonzeCommandOptions options)
        => Build(
            MonzeMessages.TitleHelp,
            MonzeEmbedColors.For(MonzeTone.Info),
            [new MessageEmbedField("Meeting", MonzeMessages.MeetingHelp(options))],
            HelpButtons());

    private static MessageContent BuildModuleHelp(string page, MonzeCommandOptions options, bool isAdmin)
        => Build(
            MonzeMessages.TitleHelp,
            MonzeEmbedColors.For(MonzeTone.Info),
            [new MessageEmbedField(string.Empty, MonzeMessages.CommandHelp(page, options, isAdmin))],
            HelpButtons());

    private static IReadOnlyList<MessageComponent> HelpButtons()
        => new ButtonBuilder()
            .AddButton(MonzeButtonId.HelpCommands, "Chung", style: (int)MessageButtonStyle.Secondary)
            .AddButton(MonzeButtonId.HelpEvent, "Sự kiện", style: (int)MessageButtonStyle.Primary)
            .AddButton(MonzeButtonId.HelpMeeting, "Meeting", style: (int)MessageButtonStyle.Success)
            .BuildComponents();

    private static IReadOnlyList<MessageComponent> WelcomeHelpButton()
        => new ButtonBuilder()
            .AddButton(MonzeButtonId.WelcomeSettings, "Cấu hình welcome", style: (int)MessageButtonStyle.Primary)
            .BuildComponents();

    private static MessageEmbedField InputField(
        string name,
        string placeholder,
        string id,
        string? value,
        bool textarea = false)
        => new(
            name,
            placeholder,
            input: new InputMessageComponent(
                id,
                placeholder,
                inputType: "text",
                defaultValue: value,
                textarea: textarea,
                required: false));

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

    private static MessageContent BuildEmbedMessage(
        WelcomeEmbedSettings draft,
        IReadOnlyList<MessageComponent>? components,
        string? saveInstruction)
    {
        var embedBuilder = new MessageEmbedBuilder()
            .SetColor(string.IsNullOrWhiteSpace(draft.Color)
                ? MonzeEmbedColors.For(MonzeTone.Info)
                : draft.Color)
            .SetTitle(string.IsNullOrWhiteSpace(draft.Title) ? "Chào mừng" : draft.Title);

        if (!string.IsNullOrWhiteSpace(draft.Url)) embedBuilder.SetUrl(draft.Url);
        if (!string.IsNullOrWhiteSpace(draft.Description)) embedBuilder.SetDescription(draft.Description);
        if (!string.IsNullOrWhiteSpace(draft.AuthorName)) embedBuilder.SetAuthor(draft.AuthorName, draft.AuthorIconUrl, draft.AuthorUrl);
        if (!string.IsNullOrWhiteSpace(draft.ThumbnailUrl)) embedBuilder.SetThumbnail(draft.ThumbnailUrl);
        if (!string.IsNullOrWhiteSpace(draft.ImageUrl)) embedBuilder.SetImage(draft.ImageUrl);
        if (!string.IsNullOrWhiteSpace(draft.FooterText)) embedBuilder.SetFooter(draft.FooterText, draft.FooterIconUrl);
        if (!string.IsNullOrWhiteSpace(draft.FieldValue))
        {
            embedBuilder.AddField(draft.FieldName ?? string.Empty, draft.FieldValue);
        }

        if (!string.IsNullOrWhiteSpace(saveInstruction))
        {
            embedBuilder.AddField("Lưu", saveInstruction);
        }

        var contentBuilder = new MessageContentBuilder().AddEmbed(embedBuilder.Build());
        if (components is not null)
        {
            contentBuilder.AddActionRow(components);
        }

        return contentBuilder.Build();
    }

    private static MessageContent AddComponents(
        MessageContent content,
        IReadOnlyList<MessageComponent> components)
    {
        var builder = new MessageContentBuilder();
        if (content.Embeds is not null)
        {
            foreach (var embed in content.Embeds)
            {
                builder.AddEmbed(embed);
            }
        }

        builder.AddActionRow(components);
        return builder.Build();
    }
}
