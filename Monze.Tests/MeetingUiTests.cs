using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;
using Monze.Ui;
using System.Text.Json;
using Xunit;

namespace Monze.Tests;

public sealed class MeetingUiTests
{
    [Fact]
    public void Meeting_schedule_form_reads_date_picker_time_and_frequency()
    {
        const string extraData = """
            {
              "monze_meeting_schedule_name": { "value": "Sprint Review" },
              "monze_meeting_schedule_date": { "value": "2026-10-01" },
              "monze_meeting_schedule_time": { "value": "18:30" },
              "monze_meeting_schedule_frequency": { "values": ["weekly"] }
            }
            """;

        Assert.True(MeetingScheduleFormParser.TryRead(
            extraData,
            out var name,
            out var date,
            out var time,
            out var kind,
            out var error), error);
        Assert.Equal("Sprint Review", name);
        Assert.Equal("01/10/2026", date);
        Assert.Equal("18:30", time);
        Assert.Equal(MeetingScheduleKind.Weekly, kind);
    }

    [Fact]
    public void Meeting_schedule_form_rejects_missing_required_values()
    {
        Assert.False(MeetingScheduleFormParser.TryRead(
            "{}",
            out _,
            out _,
            out _,
            out _,
            out var error));
        Assert.Contains("Tên cuộc họp", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Meeting_schedule_form_reads_iso_timestamp_date_without_timezone_shift()
    {
        const string extraData = "{\"monze_meeting_schedule_name\":{\"value\":\"Daily standup\"},\"monze_meeting_schedule_date\":{\"value\":\"2026-09-30T00:00:00.000Z\"},\"monze_meeting_schedule_time\":{\"value\":\"18:30\"}}";

        Assert.True(MeetingScheduleFormParser.TryRead(
            extraData,
            out var name,
            out var date,
            out var time,
            out var kind,
            out var error), error);
        Assert.Equal("Daily standup", name);
        Assert.Equal("30/09/2026", date);
        Assert.Equal("18:30", time);
        Assert.Equal(MeetingScheduleKind.Once, kind);
    }

    [Fact]
    public void Meeting_schedule_form_keeps_date_picker_and_uses_text_for_clock_input()
    {
        var root = JsonDocument.Parse(MonzeMessageBuilder.MeetingScheduleForm().RawJson).RootElement;
        var fields = root.GetProperty("embed")[0].GetProperty("fields");
        var date = fields.EnumerateArray().Single(field => field.GetProperty("name").GetString() == "Ngày");
        var time = fields.EnumerateArray().Single(field => field.GetProperty("name").GetString() == "Giờ");

        Assert.Equal((int)Mezon.Net.Client.MessageComponentType.DatePicker, date.GetProperty("inputs").GetProperty("type").GetInt32());
        Assert.Equal((int)Mezon.Net.Client.MessageComponentType.Input, time.GetProperty("inputs").GetProperty("type").GetInt32());
        Assert.Equal("text", time.GetProperty("inputs").GetProperty("component").GetProperty("type").GetString());
    }

    [Fact]
    public void Meeting_invitation_keeps_channel_link_and_optional_topic_embed()
    {
        var content = MonzeMessageBuilder.MeetingInvitation(
            new MeetingInvitation(42, "voice-room", "Sprint Review"));
        var root = JsonDocument.Parse(content.RawJson).RootElement;

        Assert.Equal(
            "@here Mọi người tham gia phòng voice-room để bắt đầu cuộc hội thoại.",
            root.GetProperty("t").GetString());
        Assert.Equal("42", root.GetProperty("hg")[0].GetProperty("channelId").GetString());
        Assert.Equal(1, root.GetProperty("embed").GetArrayLength());
    }

    [Fact]
    public void Meeting_invitation_combines_agent_instruction_and_scheduled_title()
    {
        var content = MonzeMessageBuilder.MeetingInvitation(
            new MeetingInvitation(42, "voice-room", "Sprint Review"),
            MonzeMessages.MeetingAgentInstruction);
        var root = JsonDocument.Parse(content.RawJson).RootElement;

        Assert.Equal(
            "@here Mọi người tham gia phòng voice-room để bắt đầu cuộc hội thoại.",
            root.GetProperty("t").GetString());
        Assert.Equal(
            "Cuộc hội thoại được lên lịch: Sprint Review",
            root.GetProperty("embed")[0].GetProperty("title").GetString());
        Assert.Equal(
            MonzeMessages.MeetingAgentInstruction,
            root.GetProperty("embed")[0].GetProperty("fields")[0].GetProperty("value").GetString());
        Assert.Equal(1, root.GetProperty("hg").GetArrayLength());
        Assert.Equal(31, root.GetProperty("hg")[0].GetProperty("s").GetInt32());
        Assert.Equal(41, root.GetProperty("hg")[0].GetProperty("e").GetInt32());
        Assert.True(
            MonzeEmbedColors.IsInformational(root.GetProperty("embed")[0].GetProperty("color").GetString()!));
    }

    [Fact]
    public void Immediate_meeting_keeps_agent_instruction_inside_embed()
    {
        var content = MonzeMessageBuilder.MeetingInvitation(
            new MeetingInvitation(42, "voice-room"),
            MonzeMessages.MeetingAgentInstruction);
        var root = JsonDocument.Parse(content.RawJson).RootElement;

        Assert.Equal(
            "@here Mọi người tham gia phòng voice-room để bắt đầu cuộc hội thoại.",
            root.GetProperty("t").GetString());
        Assert.Equal("Cuộc hội thoại", root.GetProperty("embed")[0].GetProperty("title").GetString());
        Assert.Equal(
            MonzeMessages.MeetingAgentInstruction,
            root.GetProperty("embed")[0].GetProperty("fields")[0].GetProperty("value").GetString());
        Assert.Equal(1, root.GetProperty("hg").GetArrayLength());
        Assert.Equal(31, root.GetProperty("hg")[0].GetProperty("s").GetInt32());
        Assert.Equal(41, root.GetProperty("hg")[0].GetProperty("e").GetInt32());
        Assert.True(
            MonzeEmbedColors.IsInformational(root.GetProperty("embed")[0].GetProperty("color").GetString()!));
    }

    [Fact]
    public void Meeting_summary_messages_use_the_requested_titles_and_two_payloads()
    {
        var delivery = MonzeMessageBuilder.MeetingSummaryMessages(
            new MeetingSummaryPresentation(
                "TÓM TẮT HỘI THOẠI #42 (🔊 voice-room)",
                "CÁC ĐẦU MỤC CÔNG VIỆC #42 (🔊 voice-room)",
                "Thứ 5, ngày 01 tháng 10 năm 2026\nCuộc hội thoại diễn ra trong 30 phút. 04:10 PM - 04:40 PM",
                "- Clan Nick: 2 phút (50%)",
                "Nội dung",
                "https://mezon.ai/developers/transcript-calls/room-1",
                [new MeetingSummaryActionItem("Clan Nick", "1. Việc cần làm")]),
            replyToMessageId: 99);

        var summary = JsonDocument.Parse(delivery.SummaryContentJson).RootElement;
        var actions = JsonDocument.Parse(delivery.ActionItemsContentJson).RootElement;

        Assert.Equal(
            "TÓM TẮT HỘI THOẠI #42 (🔊 voice-room)",
            summary.GetProperty("embed")[0].GetProperty("title").GetString());
        Assert.Equal(
            "CÁC ĐẦU MỤC CÔNG VIỆC #42 (🔊 voice-room)",
            actions.GetProperty("embed")[0].GetProperty("title").GetString());
        Assert.Equal("Người tham gia", summary.GetProperty("embed")[0].GetProperty("fields")[0].GetProperty("name").GetString());
        Assert.Equal("Clan Nick", actions.GetProperty("embed")[0].GetProperty("fields")[0].GetProperty("name").GetString());
        Assert.Equal(
            summary.GetProperty("embed")[0].GetProperty("description").GetString(),
            actions.GetProperty("embed")[0].GetProperty("description").GetString());
        Assert.Equal(99, summary.GetProperty("rpl").GetInt64());
        Assert.False(actions.TryGetProperty("rpl", out _));
        Assert.True(MonzeEmbedColors.IsInformational(summary.GetProperty("embed")[0].GetProperty("color").GetString()!));
        Assert.True(MonzeEmbedColors.IsInformational(actions.GetProperty("embed")[0].GetProperty("color").GetString()!));
    }

    [Fact]
    public void Agent_status_update_keeps_voice_link_and_here_update_metadata()
    {
        var content = MonzeMessageBuilder.MeetingAgentStatus(
            new MeetingSessionBinding(7, 1, 2, 42, false, 9, 2, null, "voice-room"),
            summarizing: false);
        var root = JsonDocument.Parse(content.RawJson).RootElement;

        Assert.Equal("@here Mọi người tham gia phòng voice-room để bắt đầu cuộc hội thoại.", root.GetProperty("t").GetString());
        Assert.Equal(1, root.GetProperty("hg").GetArrayLength());
        Assert.Equal(31, root.GetProperty("hg")[0].GetProperty("s").GetInt32());
        Assert.Equal(41, root.GetProperty("hg")[0].GetProperty("e").GetInt32());
        var here = Assert.Single(MonzeMentionMetadata.Here);
        Assert.Equal(1775731111020111321L, here.UserId);
        Assert.Equal(0, here.S);
        Assert.Equal(5, here.E);
    }

    [Fact]
    public void Agent_status_preserves_invitation_text_and_changes_only_the_embed_payload()
    {
        var content = MonzeMessageBuilder.MeetingAgentStatus(
            new MeetingSessionBinding(7, 1, 2, 42, false, 9, 2, "Sprint Review", "voice-room"),
            summarizing: true);
        var root = JsonDocument.Parse(content.RawJson).RootElement;

        Assert.Equal("@here Mọi người tham gia phòng voice-room để bắt đầu cuộc hội thoại.", root.GetProperty("t").GetString());
        Assert.Equal("Mezon Agent", root.GetProperty("embed")[0].GetProperty("title").GetString());
        Assert.Contains("Đang tóm tắt", root.GetProperty("embed")[0].GetProperty("fields")[0].GetProperty("value").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Meeting_cancel_button_round_trips_a_positive_schedule_id()
    {
        var buttonId = MeetingButtonId.CancelFor(42);

        Assert.True(MeetingButtonId.TryReadCancelId(buttonId, out var id));
        Assert.Equal(42, id);
        Assert.False(MeetingButtonId.TryReadCancelId(MeetingButtonId.CancelPrefix + "0", out _));
        Assert.False(MeetingButtonId.TryReadCancelId(MeetingButtonId.CancelPrefix + "other", out _));
    }

    [Fact]
    public void Meeting_schedule_embed_keeps_each_schedule_in_its_own_field()
    {
        var content = MonzeMessageBuilder.MeetingSchedules(
            [
                new MeetingScheduleSummary(
                    7,
                    "Sprint Review",
                    MeetingScheduleKind.Once,
                    new DateTimeOffset(2026, 9, 29, 11, 30, 0, TimeSpan.Zero),
                    "Asia/Ho_Chi_Minh",
                    1),
                new MeetingScheduleSummary(
                    8,
                    "Town hall",
                    MeetingScheduleKind.Once,
                    new DateTimeOffset(2026, 9, 30, 11, 30, 0, TimeSpan.Zero),
                    "Asia/Ho_Chi_Minh",
                    2)
            ],
            new Monze.Application.Commands.MonzeCommandOptions("*", null));

        var root = JsonDocument.Parse(content.RawJson).RootElement;
        var fields = root.GetProperty("embed")[0].GetProperty("fields");
        Assert.Equal(2, fields.GetArrayLength());
        Assert.Contains("Sprint Review", fields[0].GetProperty("name").GetString(), StringComparison.Ordinal);
        Assert.Contains("Town hall", fields[1].GetProperty("name").GetString(), StringComparison.Ordinal);
    }
}
