using Mezon.Net.Client;
using Mezon.Net.Models;
using Mezon.Net.Sdk.Entities;
using Microsoft.Extensions.Logging;
using Monze.Application;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task<long> DeliverMeetingInvitationAsync(
        Channel channel,
        MeetingInvitation invitation,
        string instruction,
        CancellationToken cancellationToken)
    {
        var messageId = await MeetingInvitationDelivery.DeliverAsync(
            invitation,
            instruction,
            channel.Id,
            _meeting,
            SendAsync,
            cancellationToken);
        _logger.LogInformation(
            "Meeting invitation delivered to text channel {TextChannelId} for voice channel {VoiceChannelId} ({VoiceChannelLabel}); MessageId={MessageId}.",
            channel.Id,
            invitation.VoiceChannelId,
            invitation.VoiceChannelLabel,
            messageId);
        return messageId;

        async Task<long> SendAsync(
            MessageContent content,
            bool mentionEveryone,
            IReadOnlyList<MessageMentionParams> mentions)
        {
            var ack = await channel.SendAsync(
                content,
                mentionEveryone: mentionEveryone,
                mentions: mentions);
            return TryReadMessageId(ack);
        }
    }
}
