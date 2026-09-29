using Mezon.Net.Models;

namespace Monze;

internal static class MonzeMentionMetadata
{
    // Mezon represents the @here marker as a special user mention.
    private static readonly MessageMentionParams[] HereValue =
    [
        new MessageMentionParams(userId: 1775731111020111321L, s: 0, e: 5)
    ];

    public static IReadOnlyList<MessageMentionParams> Here => HereValue;
}
