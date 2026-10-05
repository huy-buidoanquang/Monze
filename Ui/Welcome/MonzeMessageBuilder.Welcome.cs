using Mezon.Net.Client;
using Mezon.Net.Sdk.Builders;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;

namespace Monze.Ui;

public static partial class MonzeMessageBuilder
{
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
        if (!string.IsNullOrWhiteSpace(content.Text))
        {
            builder.SetText(content.Text);
        }

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
