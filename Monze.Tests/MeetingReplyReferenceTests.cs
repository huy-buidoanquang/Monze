using Mezon.Net.Client;
using Monze.Application;
using Xunit;

namespace Monze.Tests;

public sealed class MeetingReplyReferenceTests
{
    [Fact]
    public void Create_returns_no_reference_without_a_reply_target()
    {
        var item = CreateOutbox(replyToMessageId: null);

        Assert.Null(MeetingReplyReference.Create(item, 7, "monze"));
    }

    [Fact]
    public void Create_builds_a_visible_reply_to_the_meeting_invitation()
    {
        const long invitationMessageId = 1840651258350000001L;
        var item = CreateOutbox(
            invitationMessageId,
            replyDirectAgent: false,
            replyVoiceChannelId: 42,
            replyVoiceChannelLabel: "voice-room");

        var reference = Assert.Single(MeetingReplyReference.Create(item, 7, "monze")!);
        var content = MessageContent.Parse(reference.Content!);

        Assert.Equal(invitationMessageId, reference.MessageRefId);
        Assert.Equal(7, reference.MessageSenderId);
        Assert.Equal("monze", reference.MessageSenderUsername);
        Assert.Equal(
            "@here Mọi người tham gia phòng voice-room để bắt đầu cuộc hội thoại.",
            content.Text);
        Assert.Contains(
            "Đang tóm tắt",
            Assert.Single(Assert.Single(content.Embeds!).Fields!).Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Create_builds_a_visible_reply_to_a_direct_agent_status()
    {
        const long statusMessageId = 1840651258350000002L;
        var item = CreateOutbox(
            statusMessageId,
            replyDirectAgent: true,
            replyVoiceChannelId: 42,
            replyVoiceChannelLabel: "voice-room");

        var reference = Assert.Single(MeetingReplyReference.Create(item, 7, "monze")!);
        var content = MessageContent.Parse(reference.Content!);

        Assert.Equal(statusMessageId, reference.MessageRefId);
        Assert.Null(content.Text);
        Assert.Contains(
            "Đang tóm tắt",
            Assert.Single(Assert.Single(content.Embeds!).Fields!).Value,
            StringComparison.Ordinal);
    }

    private static DueOutbox CreateOutbox(
        long? replyToMessageId,
        bool? replyDirectAgent = null,
        long? replyVoiceChannelId = null,
        string? replyVoiceChannelLabel = null)
        => new(
            1,
            2,
            "MeetingSummary",
            "summary",
            0,
            null,
            "lease",
            DateTimeOffset.UtcNow,
            MeetingSessionId: 3,
            ReplyToMessageId: replyToMessageId,
            ReplyDirectAgent: replyDirectAgent,
            ReplyVoiceChannelId: replyVoiceChannelId,
            ReplyVoiceChannelLabel: replyVoiceChannelLabel);
}
