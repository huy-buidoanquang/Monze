using Mezon.Net.Client;
using Mezon.Net.Models;

namespace Monze;

internal sealed record WelcomeRenderedMessage(
    MessageContent Content,
    IReadOnlyList<MessageMentionParams> Mentions);
