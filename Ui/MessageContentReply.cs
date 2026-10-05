using System.Text.Json.Nodes;
using Mezon.Net.Client;

namespace Monze.Ui;

internal static class MessageContentReply
{
    internal static MessageContent Apply(MessageContent content, long replyToMessageId)
    {
        if (replyToMessageId <= 0 || content.ReplyToMessageId == replyToMessageId)
        {
            return content;
        }

        var root = JsonNode.Parse(content.ToJson()) as JsonObject;
        if (root is null)
        {
            return content;
        }

        root["rpl"] = replyToMessageId;
        return MessageContent.Parse(root.ToJsonString());
    }
}
