using System.Text.Json;
using Monze.Application;
using Xunit;

namespace Monze.Tests;

public sealed class MeetingSummaryComposerTests
{
    [Fact]
    public void Meeting_summary_record_requires_an_explicit_clan_id()
    {
        var constructor = Assert.Single(typeof(MeetingSummaryRecord).GetConstructors());
        var clanId = Assert.Single(constructor.GetParameters(), parameter => parameter.Name == "ClanId");

        Assert.False(clanId.HasDefaultValue);
    }

    [Fact]
    public async Task Summary_description_omits_zero_minutes_for_an_exact_hour()
    {
        var composer = new MeetingSummaryComposer(new StubMeetingUserProfileRepository());
        var result = new AgentSummaryResult(
            "room-duration",
            "Nội dung cuộc hội thoại.",
            null,
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero),
            [],
            [],
            [],
            "{\"room_id\":\"room-duration\"}");
        var context = CreateContext(44, result, "room-duration");

        var delivery = await composer.ComposeAsync(result, context, CancellationToken.None);
        Assert.NotNull(delivery);

        var root = JsonDocument.Parse(delivery!.SummaryContentJson).RootElement;
        Assert.Equal(
            "Thứ 5, ngày 01 tháng 10 năm 2026\nCuộc hội thoại diễn ra trong 1 giờ. 04:00 PM - 05:00 PM",
            root.GetProperty("embed")[0].GetProperty("description").GetString());
    }

    [Fact]
    public async Task Summary_description_never_reports_zero_minutes_for_a_positive_duration()
    {
        var composer = new MeetingSummaryComposer(new StubMeetingUserProfileRepository());
        var result = new AgentSummaryResult(
            "room-short",
            "Nội dung cuộc hội thoại.",
            null,
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 9, 0, 10, TimeSpan.Zero),
            [],
            [],
            [],
            "{\"room_id\":\"room-short\"}");

        var delivery = await composer.ComposeAsync(
            result,
            CreateContext(46, result, "room-short"),
            CancellationToken.None);

        Assert.NotNull(delivery);
        var root = JsonDocument.Parse(delivery!.SummaryContentJson).RootElement;
        Assert.Contains(
            "Cuộc hội thoại diễn ra trong 1 phút.",
            root.GetProperty("embed")[0].GetProperty("description").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_does_not_display_a_raw_user_id_when_profile_labels_are_missing()
    {
        var composer = new MeetingSummaryComposer(new StubMeetingUserProfileRepository(
            static (clanId, userId) => new UserProfileSnapshot(
                clanId,
                userId,
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow)));
        var result = new AgentSummaryResult(
            "room-anonymous",
            "Nội dung cuộc hội thoại.",
            null,
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 9, 10, 0, TimeSpan.Zero),
            ["100"],
            [new AgentSpeechDuration("100", 60)],
            [new AgentActionItemGroup("100", ["Theo dõi công việc"])],
            "{\"room_id\":\"room-anonymous\"}");
        var context = CreateContext(45, result, "room-anonymous");

        var delivery = await composer.ComposeAsync(result, context, CancellationToken.None);
        Assert.NotNull(delivery);

        var summary = JsonDocument.Parse(delivery!.SummaryContentJson).RootElement;
        var actions = JsonDocument.Parse(delivery.ActionItemsContentJson).RootElement;
        var participantText = summary.GetProperty("embed")[0].GetProperty("fields")[0].GetProperty("value").GetString();
        var actionName = actions.GetProperty("embed")[0].GetProperty("fields")[0].GetProperty("name").GetString();
        Assert.Contains("Thành viên 1", participantText, StringComparison.Ordinal);
        Assert.DoesNotContain("- 100:", participantText, StringComparison.Ordinal);
        Assert.Equal("Thành viên 1", actionName);
    }

    [Fact]
    public async Task Summary_does_not_invent_one_second_for_a_silent_participant()
    {
        var composer = new MeetingSummaryComposer(new StubMeetingUserProfileRepository(
            static (clanId, userId) => new UserProfileSnapshot(
                clanId,
                userId,
                null,
                userId == 100 ? "Người nói" : "Người nghe",
                null,
                null,
                DateTimeOffset.UtcNow)));
        var result = new AgentSummaryResult(
            "room-silent",
            "Nội dung cuộc hội thoại.",
            null,
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 9, 1, 0, TimeSpan.Zero),
            ["100", "200"],
            [
                new AgentSpeechDuration("100", 18.72),
                new AgentSpeechDuration("200", 0)
            ],
            [],
            "{\"room_id\":\"room-silent\"}");

        var delivery = await composer.ComposeAsync(
            result,
            CreateContext(47, result, "room-silent"),
            CancellationToken.None);

        Assert.NotNull(delivery);
        var root = JsonDocument.Parse(delivery!.SummaryContentJson).RootElement;
        var participants = root.GetProperty("embed")[0]
            .GetProperty("fields")[0]
            .GetProperty("value")
            .GetString();
        Assert.Contains("- Người nghe: 0 giây (0%)", participants, StringComparison.Ordinal);
        Assert.DoesNotContain("- Người nghe: 1 giây", participants, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_excludes_the_numeric_mezon_agent_identity()
    {
        var composer = new MeetingSummaryComposer(new StubMeetingUserProfileRepository(
            static (clanId, userId) => new UserProfileSnapshot(
                clanId,
                userId,
                null,
                $"Thành viên {userId}",
                null,
                null,
                DateTimeOffset.UtcNow)));
        var result = new AgentSummaryResult(
            "room-agent",
            "Nội dung cuộc hội thoại.",
            null,
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 9, 1, 0, TimeSpan.Zero),
            ["100", MezonAgentIdentity.UserIdText],
            [
                new AgentSpeechDuration("100", 18.72),
                new AgentSpeechDuration(MezonAgentIdentity.UserIdText, 0)
            ],
            [new AgentActionItemGroup(MezonAgentIdentity.UserIdText, ["Nội bộ Agent"])],
            "{\"room_id\":\"room-agent\"}");

        var delivery = await composer.ComposeAsync(
            result,
            CreateContext(48, result, "room-agent"),
            CancellationToken.None);

        Assert.NotNull(delivery);
        var summary = JsonDocument.Parse(delivery!.SummaryContentJson).RootElement;
        var actions = JsonDocument.Parse(delivery.ActionItemsContentJson).RootElement;
        var participants = summary.GetProperty("embed")[0]
            .GetProperty("fields")[0]
            .GetProperty("value")
            .GetString();
        var actionFields = actions.GetProperty("embed")[0].GetProperty("fields");
        Assert.Contains("Thành viên 100", participants, StringComparison.Ordinal);
        Assert.DoesNotContain(MezonAgentIdentity.UserIdText, participants, StringComparison.Ordinal);
        Assert.DoesNotContain("Nội bộ Agent", actions.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("Chưa có đầu mục công việc.", actionFields[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Summary_description_formats_sunday_without_a_thu_prefix()
    {
        var composer = new MeetingSummaryComposer(new StubMeetingUserProfileRepository());
        var result = new AgentSummaryResult(
            "room-sunday",
            "Nội dung cuộc hội thoại.",
            "toàn bộ transcript",
            new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 4, 9, 10, 0, TimeSpan.Zero),
            [],
            [],
            [],
            "{\"room_id\":\"room-sunday\"}");
        var context = new MeetingSummaryContext(
            43,
            7,
            8,
            9,
            "room-sunday",
            null,
            "voice-room",
            result.CreatedAt,
            result.FinalizedAt,
            null,
            null);

        var delivery = await composer.ComposeAsync(result, context, CancellationToken.None);
        Assert.NotNull(delivery);

        var root = JsonDocument.Parse(delivery!.SummaryContentJson).RootElement;
        Assert.Equal(
            "Chủ nhật, ngày 04 tháng 10 năm 2026\nCuộc hội thoại diễn ra trong 10 phút. 04:00 PM - 04:10 PM",
            root.GetProperty("embed")[0].GetProperty("description").GetString());
    }

    [Fact]
    public async Task Summary_description_uses_two_lines_and_covers_a_two_day_meeting()
    {
        var composer = new MeetingSummaryComposer(new StubMeetingUserProfileRepository());
        var result = new AgentSummaryResult(
            "room-1",
            "Nội dung cuộc hội thoại.",
            "toàn bộ transcript",
            new DateTimeOffset(2026, 10, 1, 16, 10, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 2, 0, 20, 0, TimeSpan.Zero),
            ["100"],
            [new AgentSpeechDuration("100", 600)],
            [],
            "{\"room_id\":\"room-1\"}");
        var context = new MeetingSummaryContext(
            42,
            7,
            8,
            9,
            "room-1",
            "Sprint Review",
            "voice-room",
            result.CreatedAt,
            result.FinalizedAt,
            null,
            null);

        var delivery = await composer.ComposeAsync(result, context, CancellationToken.None);
        Assert.NotNull(delivery);

        var root = JsonDocument.Parse(delivery!.SummaryContentJson).RootElement;
        var embed = root.GetProperty("embed")[0];
        Assert.Equal("TÓM TẮT HỘI THOẠI #42 (🔊 voice-room)", embed.GetProperty("title").GetString());
        Assert.Equal(
            "Thứ 5, ngày 01 tháng 10 năm 2026 - Thứ 6, ngày 02 tháng 10 năm 2026\nCuộc hội thoại diễn ra trong 8 giờ 10 phút. 11:10 PM (01/10) - 07:20 AM (02/10)",
            embed.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Stored_summary_record_rebuilds_the_full_summary_messages()
    {
        var composer = new MeetingSummaryComposer(new StubMeetingUserProfileRepository());
        const string storedTranscript = """
            {
              "room_id":"stored-room",
              "created_at":"2026-10-01T09:00:00Z",
              "finalized_at":"2026-10-01T09:05:00Z",
              "participants":[],
              "speech_durations":[],
              "summary_data":{"summary":"Nội dung đã lưu.","action_items":{}}
            }
            """;
        var record = new MeetingSummaryRecord(
            88,
            8,
            9,
            "Nội dung đã lưu.",
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 9, 5, 0, TimeSpan.Zero),
            null,
            7,
            "stored-room",
            "Stored meeting",
            "voice-room",
            storedTranscript);

        var delivery = await composer.ComposeAsync(record, CancellationToken.None);

        Assert.NotNull(delivery);
        var root = JsonDocument.Parse(delivery!.SummaryContentJson).RootElement;
        Assert.Equal(
            "TÓM TẮT HỘI THOẠI #88 (🔊 voice-room)",
            root.GetProperty("embed")[0].GetProperty("title").GetString());
    }

    private static MeetingSummaryContext CreateContext(
        long sessionId,
        AgentSummaryResult result,
        string roomId)
        => new(
            sessionId,
            7,
            8,
            9,
            roomId,
            null,
            "voice-room",
            result.CreatedAt,
            result.FinalizedAt,
            null,
            null);
}
