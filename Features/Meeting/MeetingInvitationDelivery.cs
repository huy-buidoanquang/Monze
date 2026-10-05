using Mezon.Net.Client;
using Mezon.Net.Models;
using Monze.Application;
using Monze.Ui;

namespace Monze;

internal static class MeetingInvitationDelivery
{
    public static async Task<long> DeliverAsync(
        MeetingInvitation invitation,
        string instruction,
        long textChannelId,
        IMeetingRepository meeting,
        Func<MessageContent, bool, IReadOnlyList<MessageMentionParams>, Task<long>> sendAsync,
        CancellationToken cancellationToken)
    {
        var messageId = await sendAsync(
            MonzeMessageBuilder.MeetingInvitation(invitation, instruction),
            true,
            MonzeMentionMetadata.Here);
        if (messageId > 0 && invitation.SessionId is long sessionId)
        {
            await meeting.SetSessionInvitationMessageAsync(
                sessionId,
                textChannelId,
                messageId,
                cancellationToken);
        }

        return messageId;
    }
}
