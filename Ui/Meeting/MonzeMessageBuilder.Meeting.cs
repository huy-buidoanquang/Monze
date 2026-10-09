using Mezon.Net.Client;
using Mezon.Net.Sdk.Builders;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;

namespace Monze.Ui;

public static partial class MonzeMessageBuilder
{
    public static MessageContent MeetingInvitation(
        MeetingInvitation invitation,
        string? instruction = null)
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
                prefix.Length + label.Length);

        if (!string.IsNullOrWhiteSpace(instruction)
            || !string.IsNullOrWhiteSpace(invitation.ConversationTitle))
        {
            var embedTitle = string.IsNullOrWhiteSpace(invitation.ConversationTitle)
                ? "Cuộc hội thoại"
                : $"Cuộc hội thoại được lên lịch: {invitation.ConversationTitle!.Trim()}";
            var embed = new MessageEmbedBuilder()
                .SetTitle(embedTitle)
                .SetColor(MonzeEmbedColors.For(MonzeTone.Info));
            if (!string.IsNullOrWhiteSpace(instruction))
            {
                embed.AddField(string.Empty, instruction.Trim());
            }

            builder.AddEmbed(embed.Build());
        }

        return builder.Build();
    }
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

    public static MeetingSummaryDelivery MeetingSummaryMessages(
        MeetingSummaryPresentation presentation,
        long? replyToMessageId = null)
    {
        var summaryBuilder = new MessageContentBuilder();
        var actionsBuilder = new MessageContentBuilder();
        if (replyToMessageId is long messageId && messageId > 0)
        {
            summaryBuilder.SetReplyToMessageId(messageId);
        }

        var summary = summaryBuilder
            .AddEmbed(new MessageEmbedBuilder()
                .SetTitle(presentation.Title)
                .SetDescription(presentation.Description)
                .SetUrl(presentation.TranscriptUrl)
                .SetColor(MonzeEmbedColors.For(MonzeTone.Info))
                .AddField("Người tham gia", presentation.Participants)
                .AddField("Nội dung cuộc hội thoại", presentation.Content)
                .AddField("Xem chi tiết", presentation.TranscriptUrl)
                .Build())
            .Build();

        var actions = new MessageEmbedBuilder()
            .SetTitle(presentation.ActionItemsTitle)
            .SetDescription(presentation.Description)
            .SetUrl(presentation.TranscriptUrl)
            .SetColor(MonzeEmbedColors.For(MonzeTone.Info));
        foreach (var item in presentation.ActionItems.Take(25))
        {
            actions.AddField(item.Name, item.Value);
        }

        return new MeetingSummaryDelivery(
            summary.ToJson(),
            actionsBuilder.AddEmbed(actions.Build()).Build().ToJson());
    }
    public static MessageContent AgentWaiting()
        => Card("Mezon Agent", "Agent đã được bật trong voice. Đang chờ cuộc hội thoại kết thúc.", MonzeTone.Info);

    public static MessageContent AgentSummarizing()
        => Card("Mezon Agent", "Cuộc hội thoại đã kết thúc. Đang tóm tắt…", MonzeTone.Info);

    public static MessageContent MeetingAgentStatus(
        MeetingSessionBinding binding,
        bool summarizing)
        => MeetingAgentStatus(
            binding.VoiceChannelId,
            binding.VoiceChannelLabel,
            summarizing);

    internal static MessageContent MeetingAgentStatus(
        long voiceChannelId,
        string? voiceChannelLabel,
        bool summarizing)
    {
        var label = string.IsNullOrWhiteSpace(voiceChannelLabel)
            ? "phòng voice"
            : voiceChannelLabel.Trim().TrimStart('#');
        var prefix = "@here Mọi người tham gia phòng ";
        var text = $"{prefix}{label} để bắt đầu cuộc hội thoại.";
        var status = summarizing
            ? "Cuộc hội thoại đã kết thúc. Đang tóm tắt…"
            : "Agent đã được bật trong voice. Đang chờ cuộc hội thoại kết thúc.";
        var embed = new MessageEmbedBuilder()
            .SetTitle("Mezon Agent")
            .SetColor(MonzeEmbedColors.For(MonzeTone.Info))
            .AddField(string.Empty, status)
            .Build();
        return new MessageContentBuilder()
            .SetText(text)
            .AddHashtag(
                voiceChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                prefix.Length,
                prefix.Length + label.Length)
            .AddEmbed(embed)
            .Build();
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

    /// <summary>
    /// CAND-27: told privately to a schedule's requester when no voice room is
    /// free at its time, with the schedule's cancel button.
    /// </summary>
    public static MessageContent SchedulePostponed(string name, long scheduleId)
        => BuildWithRows(
            MonzeMessages.TitleMeeting,
            MonzeEmbedColors.For(MonzeTone.Warn),
            [new MessageEmbedField("Lịch họp", MonzeMessages.SchedulePostponed(name, scheduleId))],
            [new ButtonBuilder()
                .AddButton(MeetingButtonId.CancelFor(scheduleId), $"Hủy #{scheduleId}", style: (int)MessageButtonStyle.Danger)
                .BuildComponents()]);

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
                inputType: "text",
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
        => LocalSchedule.Describe(schedule.NextRunAt, schedule.TimeZoneId);

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
}
