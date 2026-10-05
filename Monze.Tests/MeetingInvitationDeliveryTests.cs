using Mezon.Net.Client;
using Mezon.Net.Models;
using Monze.Application;
using System.Text.Json;
using Xunit;

namespace Monze.Tests;

public sealed class MeetingInvitationDeliveryTests
{
    [Fact]
    public async Task Delivery_sends_here_invitation_and_binds_the_acknowledged_message()
    {
        var dependencies = new MonzeAppTestDependencies();
        MessageContent? sentContent = null;
        bool? sentMentionEveryone = null;
        IReadOnlyList<MessageMentionParams>? sentMentions = null;

        var messageId = await MeetingInvitationDelivery.DeliverAsync(
            new MeetingInvitation(42, "voice-room", SessionId: 7),
            "Bật Agent để bắt đầu.",
            21,
            dependencies,
            (content, mentionEveryone, mentions) =>
            {
                sentContent = content;
                sentMentionEveryone = mentionEveryone;
                sentMentions = mentions;
                return Task.FromResult(99L);
            },
            CancellationToken.None);

        Assert.Equal(99, messageId);
        Assert.True(sentMentionEveryone);
        var mention = Assert.Single(sentMentions!);
        Assert.Equal(1775731111020111321L, mention.UserId);
        Assert.Equal(0, mention.S);
        Assert.Equal(5, mention.E);
        Assert.Equal((7L, 21L, 99L), dependencies.MeetingInvitationBinding);

        var payload = JsonDocument.Parse(sentContent!.RawJson).RootElement;
        Assert.Equal(
            "@here Mọi người tham gia phòng voice-room để bắt đầu cuộc hội thoại.",
            payload.GetProperty("t").GetString());
        Assert.Equal(
            "Bật Agent để bắt đầu.",
            payload.GetProperty("embed")[0].GetProperty("fields")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Delivery_does_not_bind_a_message_without_an_acknowledged_id()
    {
        var dependencies = new MonzeAppTestDependencies();

        var messageId = await MeetingInvitationDelivery.DeliverAsync(
            new MeetingInvitation(42, "voice-room", SessionId: 7),
            "Bật Agent để bắt đầu.",
            21,
            dependencies,
            static (_, _, _) => Task.FromResult(0L),
            CancellationToken.None);

        Assert.Equal(0, messageId);
        Assert.Null(dependencies.MeetingInvitationBinding);
    }
}
