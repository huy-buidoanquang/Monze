using Mezon.Net.Models;
using Monze.Application;
using Monze.Ui;

namespace Monze;

internal static class MeetingReplyReference
{
    internal static MessageRefParams[]? Create(
        DueOutbox item,
        long botId,
        string? botUsername)
    {
        if (item.ReplyToMessageId is not long replyToMessageId
            || replyToMessageId <= 0)
        {
            return null;
        }

        var referencedContent = item.ReplyDirectAgent == true
            || item.ReplyVoiceChannelId is not long voiceChannelId
            || voiceChannelId <= 0
                ? MonzeMessageBuilder.AgentSummarizing()
                : MonzeMessageBuilder.MeetingAgentStatus(
                    voiceChannelId,
                    item.ReplyVoiceChannelLabel,
                    summarizing: true);

        return
        [
            new MessageRefParams(
                messageRefId: replyToMessageId,
                content: referencedContent.ToJson(),
                hasAttachment: false,
                refType: 0,
                messageSenderId: botId,
                messageSenderUsername: string.IsNullOrWhiteSpace(botUsername)
                    ? null
                    : botUsername)
        ];
    }
}
