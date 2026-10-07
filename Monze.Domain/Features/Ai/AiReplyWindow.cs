namespace Monze.Domain;

public static class AiReplyWindow
{
    public static bool Contains(
        DateTimeOffset? messageCreatedAt,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (messageCreatedAt is not { } createdAt || maximumAge < TimeSpan.Zero)
        {
            return false;
        }

        var age = now - createdAt;
        return age >= TimeSpan.Zero && age <= maximumAge;
    }

    // The replied message is always summarized because the member chose it.
    // Later bot commands and the bot's own output are not conversation, so
    // they must not leak into the summary input.
    public static bool IsConversationMessage(
        long messageId,
        long anchorId,
        long senderId,
        long botId,
        string text,
        string commandPrefix)
    {
        if (messageId == anchorId)
        {
            return true;
        }

        if (botId > 0 && senderId == botId)
        {
            return false;
        }

        return string.IsNullOrEmpty(commandPrefix)
            || !text.AsSpan().TrimStart().StartsWith(commandPrefix, StringComparison.Ordinal);
    }
}
