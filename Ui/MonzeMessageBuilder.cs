using Mezon.Net.Client;
using Mezon.Net.Sdk.Builders;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;

namespace Monze.Ui;

public static class MonzeMessageBuilder
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

    public static MessageContent MeetingInvitation(MeetingInvitation invitation)
    {
        var label = string.IsNullOrWhiteSpace(invitation.VoiceChannelLabel)
            ? "phòng voice"
            : invitation.VoiceChannelLabel.Trim();
        var prefix = "@here Mọi người tham gia phòng ";
        var text = $"{prefix}{label} để bắt đầu cuộc hội thoại.";
        var builder = new MessageContentBuilder()
            .SetText(text)
            .AddHashtag(
                invitation.VoiceChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                prefix.Length,
                prefix.Length + label.Length + 1);

        if (!string.IsNullOrWhiteSpace(invitation.ConversationTitle))
        {
            builder.AddEmbed(
                new MessageEmbedBuilder()
                    .SetTitle("Cuộc họp")
                    .AddField("Chủ đề", invitation.ConversationTitle.Trim())
                    .Build());
        }

        return builder.Build();
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

    public static MessageContent MeetingSummary(string summary, long? replyToMessageId = null)
    {
        var builder = new MessageContentBuilder();
        if (replyToMessageId is long messageId)
        {
            builder.SetReplyToMessageId(messageId);
        }

        return builder
            .AddEmbed(new MessageEmbedBuilder()
                .SetTitle("Tóm tắt cuộc họp")
                .SetColor(MonzeEmbedColors.For(MonzeTone.Info))
                .AddField(string.Empty, string.IsNullOrWhiteSpace(summary) ? MonzeMessages.NoSummary : summary)
                .Build())
            .Build();
    }

    public static MessageContent AgentWaiting()
        => Card("Agent", "Agent đã được bật trong voice. Đang chờ cuộc hội thoại kết thúc.", MonzeTone.Info);

    public static MessageContent AgentSummarizing()
        => Card("Agent", "Cuộc hội thoại đã kết thúc. Đang tóm tắt…", MonzeTone.Info);

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

    public static MessageContent WelcomeCard(WelcomeSettings settings)
    {
        var draft = settings.Embed;
        if (draft is null)
        {
            draft = new WelcomeEmbedSettings(Title: "Chào mừng");
        }

        return BuildEmbedMessage(
            draft,
            components: null,
            saveInstruction: null,
            messageText: settings.Text);
    }

    public static MessageContent WelcomeSettings(WelcomeSettings? settings)
        => WelcomeSettings(settings, WelcomeSetupSection.General);

    public static MessageContent WelcomeSettings(
        WelcomeSettings? settings,
        WelcomeSetupSection section)
    {
        var current = settings ?? new WelcomeSettings(false, null, 0);
        var draft = current.Embed ?? new WelcomeEmbedSettings(
            Title: "Chào mừng");
        var fields = new List<MessageEmbedField>
        {
            new(
                "Trạng thái",
                "Chọn Bật hoặc Tắt.",
                input: new RadioMessageComponent(
                    MonzeButtonId.WelcomeStatus,
                    [
                        new MessageRadioOption("Bật", "on", Name: MonzeButtonId.WelcomeStatus),
                        new MessageRadioOption("Tắt", "off", Name: MonzeButtonId.WelcomeStatus)
                    ],
                    maxOptions: 1)),
        };

        // The web client keeps uncontrolled inputs by their positional React
        // key. Reserve disjoint, invisible rows so a section switch unmounts
        // the previous inputs instead of reusing them for another field.
        for (var i = 0; i < RefreshSlotCount(section); i++)
        {
            fields.Add(new MessageEmbedField(
                string.Empty,
                string.Empty,
                input: new AnimationMessageComponent(
                    $"monze_welcome_refresh_{(int)section}_{i}")));
        }

        switch (section)
        {
            case WelcomeSetupSection.General:
                fields.Add(InputField("Tiêu đề", "Tiêu đề embed", MonzeButtonId.WelcomeTitle, draft.Title));
                fields.Add(InputField("Nội dung", "Nội dung chính; hỗ trợ {user}, {role:Tên}, {channel:tên-kênh}", MonzeButtonId.WelcomeDescription, draft.Description, textarea: true));
                fields.Add(InputField("Liên kết", "Chỉ nhận URL https://", MonzeButtonId.WelcomeUrl, draft.Url));
                fields.Add(InputField("Màu", "Mã hex, ví dụ #5865F2", MonzeButtonId.WelcomeColor, draft.Color));
                break;
            case WelcomeSetupSection.Images:
                fields.Add(InputField("Ảnh nhỏ", "Thumbnail URL https://", MonzeButtonId.WelcomeThumbnail, draft.ThumbnailUrl));
                fields.Add(InputField("Ảnh lớn", "Image URL https://", MonzeButtonId.WelcomeImage, draft.ImageUrl));
                break;
            case WelcomeSetupSection.Author:
                fields.Add(InputField("Tác giả", "Tên tác giả", MonzeButtonId.WelcomeAuthor, draft.AuthorName));
                fields.Add(InputField("Icon", "URL https://", MonzeButtonId.WelcomeAuthorIcon, draft.AuthorIconUrl));
                fields.Add(InputField("Liên kết", "URL https://", MonzeButtonId.WelcomeAuthorUrl, draft.AuthorUrl));
                break;
            case WelcomeSetupSection.Advanced:
                fields.Add(InputField("Footer", "Nội dung footer", MonzeButtonId.WelcomeFooter, draft.FooterText));
                fields.Add(InputField("Icon footer", "URL https://", MonzeButtonId.WelcomeFooterIcon, draft.FooterIconUrl));
                fields.Add(InputField("Tiêu đề phụ", "Tên field phụ", MonzeButtonId.WelcomeFieldName, draft.FieldName));
                fields.Add(InputField("Nội dung phụ", "Nội dung field phụ", MonzeButtonId.WelcomeFieldValue, draft.FieldValue, textarea: true));
                break;
        }

        var categories = new ButtonBuilder()
            .AddButton(MonzeButtonId.WelcomeGeneral, "Chung", style: (int)MessageButtonStyle.Secondary)
            .AddButton(MonzeButtonId.WelcomeImages, "Hình ảnh", style: (int)MessageButtonStyle.Secondary)
            .AddButton(MonzeButtonId.WelcomeAuthorSection, "Tác giả", style: (int)MessageButtonStyle.Secondary)
            .AddButton(MonzeButtonId.WelcomeAdvanced, "Mở rộng", style: (int)MessageButtonStyle.Secondary)
            .BuildComponents();
        var actions = new ButtonBuilder()
            .AddButton(MonzeButtonId.WelcomePreview, "Preview", style: (int)MessageButtonStyle.Primary)
            .AddButton(MonzeButtonId.WelcomeSave, "Submit", style: (int)MessageButtonStyle.Success)
            .AddButton(MonzeButtonId.WelcomeCancel, "Huỷ", style: (int)MessageButtonStyle.Danger)
            .BuildComponents();
        return BuildWithRows(
            MonzeMessages.TitleWelcomeSetup,
            MonzeEmbedColors.For(MonzeTone.Info),
            fields,
            [categories, actions]);
    }

    public static MessageContent WelcomePreview(
        WelcomeEmbedSettings draft,
        MonzeCommandOptions? options = null,
        string? token = null,
        string? messageText = null)
    {
        var content = BuildEmbedMessage(
            draft,
            components: null,
            saveInstruction: token is null
                ? null
                : MonzeMessages.WelcomeDraftReady(),
            messageText: messageText);
        var buttons = new ButtonBuilder()
            .AddButton(MonzeButtonId.WelcomeSettings, "Chỉnh sửa", style: (int)MessageButtonStyle.Secondary)
            .AddButton(
                token is null ? MonzeButtonId.WelcomeSave : MonzeButtonId.WelcomeSaveFor(token),
                "Submit",
                style: (int)MessageButtonStyle.Success)
            .BuildComponents();
        return AddComponents(content, buttons);
    }

    public static MessageContent MeetingSchedules(
        IReadOnlyList<MeetingScheduleSummary> schedules,
        MonzeCommandOptions options)
    {
        var fields = new List<MessageEmbedField>(Math.Min(20, schedules.Count) + 1);
        if (schedules.Count == 0)
        {
            fields.Add(new MessageEmbedField("Lịch họp", MonzeMessages.MeetingScheduleEmpty));
        }
        else
        {
            for (var i = 0; i < schedules.Count && i < 20; i++)
            {
                var schedule = schedules[i];
                fields.Add(new MessageEmbedField(
                    $"#{schedule.Id} · {TrimLabel(schedule.Name)}",
                    $"Lần tới: {FormatScheduleTime(schedule)}\nKiểu: {FormatScheduleKind(schedule)}\n{options.DirectCommand(MonzeCommandNames.Meeting, "cancel", schedule.Id.ToString())}"));
            }
        }

        var rows = new List<IReadOnlyList<MessageComponent>>(1 + ((schedules.Count + 4) / 5));
        rows.Add(new ButtonBuilder()
            .AddButton(MeetingButtonId.StartNow, "Bắt đầu ngay", style: (int)MessageButtonStyle.Success)
            .AddButton(MeetingButtonId.Schedule, "Lên lịch", style: (int)MessageButtonStyle.Primary)
            .AddButton(MeetingButtonId.Refresh, "Làm mới", style: (int)MessageButtonStyle.Secondary)
            .AddButton(MeetingButtonId.Help, "Hướng dẫn", style: (int)MessageButtonStyle.Primary)
            .BuildComponents());

        for (var offset = 0; offset < schedules.Count && offset < 20; offset += 5)
        {
            var row = new ButtonBuilder();
            var end = Math.Min(offset + 5, schedules.Count);
            for (var i = offset; i < end; i++)
            {
                row.AddButton(
                    MeetingButtonId.CancelFor(schedules[i].Id),
                    $"Hủy #{schedules[i].Id}",
                    style: (int)MessageButtonStyle.Danger);
            }

            rows.Add(row.BuildComponents());
        }

        return BuildWithRows(
            MonzeMessages.TitleMeeting,
            MonzeEmbedColors.For(MonzeTone.Info),
            fields,
            rows);
    }

    public static MessageContent MeetingScheduleForm(string? error = null)
    {
        var fields = new List<MessageEmbedField>(5);
        if (!string.IsNullOrWhiteSpace(error))
        {
            fields.Add(new MessageEmbedField("Lỗi", error));
        }

        fields.Add(new MessageEmbedField(
            "Tên cuộc họp",
            "Nhập tên cuộc họp.",
            input: new InputMessageComponent(
                MeetingButtonId.ScheduleName,
                "Ví dụ: Daily standup",
                required: true)));
        fields.Add(new MessageEmbedField(
            "Ngày",
            "Chọn ngày theo dd/MM/yyyy.",
            input: new DatePickerMessageComponent(MeetingButtonId.ScheduleDate)));
        fields.Add(new MessageEmbedField(
            "Giờ",
            "Nhập theo HH:mm, múi giờ Asia/Ho_Chi_Minh.",
            input: new InputMessageComponent(
                MeetingButtonId.ScheduleTime,
                "18:30",
                inputType: "time",
                required: true)));
        fields.Add(new MessageEmbedField(
            "Tần suất",
            "Chọn cách lặp.",
            input: new SelectMessageComponent(
                MeetingButtonId.ScheduleFrequency,
                [
                    new MessageSelectOption("Một lần", "once", Default: true),
                    new MessageSelectOption("Hằng ngày", "daily"),
                    new MessageSelectOption("Hằng tuần", "weekly")
                ],
                placeholder: "Chọn tần suất")));

        var actions = new ButtonBuilder()
            .AddButton(MeetingButtonId.ScheduleSubmit, "Lưu lịch", style: (int)MessageButtonStyle.Success)
            .AddButton(MeetingButtonId.ScheduleCancel, "Huỷ", style: (int)MessageButtonStyle.Danger)
            .BuildComponents();
        return BuildWithRows(
            "Lên lịch meeting",
            MonzeEmbedColors.For(MonzeTone.Info),
            fields,
            [actions]);
    }

    private static string FormatScheduleTime(MeetingScheduleSummary schedule)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
            return $"{TimeZoneInfo.ConvertTime(schedule.NextRunAt, zone):dd/MM/yyyy HH:mm} ({schedule.TimeZoneId})";
        }
        catch (TimeZoneNotFoundException)
        {
            return $"{schedule.NextRunAt:dd/MM/yyyy HH:mm} UTC";
        }
        catch (InvalidTimeZoneException)
        {
            return $"{schedule.NextRunAt:dd/MM/yyyy HH:mm} UTC";
        }
    }

    private static string FormatScheduleKind(MeetingScheduleSummary schedule)
        => schedule.Kind switch
            {
                MeetingScheduleKind.Daily => "hằng ngày",
                MeetingScheduleKind.Weekly => "hằng tuần",
                _ => "một lần"
            };

    private static string TrimLabel(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "Cuộc họp" : value.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..77] + "...";
    }

    private static MessageContent BuildCommandsHelp(
        MonzeCommandOptions options,
        bool isAdmin,
        bool canManageWelcome,
        bool isOwner)
    {
        var entries = MonzeHelpCatalog.Commands(options, isAdmin, canManageWelcome);
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

    private static int RefreshSlotCount(WelcomeSetupSection section)
        => section switch
        {
            WelcomeSetupSection.General => 0,
            WelcomeSetupSection.Images => 4,
            WelcomeSetupSection.Author => 8,
            WelcomeSetupSection.Advanced => 12,
            _ => 0
        };

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

    private static MessageContent BuildEmbedMessage(
        WelcomeEmbedSettings draft,
        IReadOnlyList<MessageComponent>? components,
        string? saveInstruction,
        string? messageText = null)
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
        if (!string.IsNullOrWhiteSpace(messageText))
        {
            contentBuilder.SetText(messageText);
        }
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
