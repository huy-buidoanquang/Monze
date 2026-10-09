using CsCheck;
using Monze.Domain;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Domain;

/// <summary>
/// G10: AI rules. AiBudget consumes only positive requests that fit the
/// daily cap; AiReplyWindow accepts a replied message only inside the window
/// and keeps bot output and later commands out of the conversation.
/// Known gap CAND-07: AiBudget adds in int, so usedToday + requested can
/// overflow and pass the cap. Production budgets are enforced in SQL
/// (ai_usage) and AiBudget has no production caller.
/// </summary>
public sealed class G10AiRulesProperties
{
    private static readonly Gen<int> Amount = Gen.OneOf(
        Gen.Int[-5, 5],
        Gen.Int[0, 10_000],
        Gen.Int[int.MaxValue - 10, int.MaxValue],
        Gen.Int[int.MinValue, int.MinValue + 10]);

    [Fact]
    [Req("REQ-AI-001")]
    public void Budget_consumes_only_requests_that_fit_the_cap()
    {
        var caps = Gen.OneOf(Gen.OneOfConst(0, 1, 2_000, int.MaxValue), Gen.Int[0, 100_000]);
        PropertyRun.Run(
            "G10",
            Gen.Select(Amount, Amount, caps),
            static value =>
            {
                var (used, requested, cap) = value;
                var total = (long)used + requested;
                var expected = requested > 0 && total <= cap;
                var actual = AiBudget.TryConsume(used, requested, cap, out var next);
                var nextOk = actual ? next == total : next == used;
                var overflow = total > int.MaxValue || total < int.MinValue;
                var tags = new Dictionary<string, string>
                {
                    ["expected"] = expected ? "consume" : "reject",
                    ["overflow"] = overflow ? "yes" : "no"
                };
                var input = $"used={used} requested={requested} cap={cap}";
                var correct = actual == expected && nextOk;
                if (overflow)
                {
                    return correct
                        ? PropertyResult.Pass(input, tags, "CAND-07")
                        : PropertyResult.Known("CAND-07", input, tags, $"expected {(expected ? "consume" : "reject")}, got {(actual ? "consume" : "reject")} next={next}");
                }

                return PropertyResult.Check(correct, input, tags, () => $"expected {(expected ? "consume" : "reject")}, got {(actual ? "consume" : "reject")} next={next}");
            },
            iterations: 50_000,
            declare: static ledger => ledger
                .Dimension("expected", "consume", "reject")
                .Dimension("overflow", "yes", "no")
                .Infeasible("expected", "consume", "overflow", "yes"),
            knownDefects: ["CAND-07"]);
    }

    [Fact]
    [Req("REQ-AI-001")]
    [Covers("msg:AiHistoryWindow")]
    public void Reply_window_and_conversation_filter_follow_their_rules()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var age = Gen.OneOf(Gen.Long[-TimeSpan.TicksPerHour, TimeSpan.TicksPerDay], Gen.Long[-2, 2]);
        var windows = Gen.OneOf(Gen.Long[-TimeSpan.TicksPerMinute, TimeSpan.TicksPerDay], Gen.Const(0L));
        var text = Gen.OneOfConst("hello", "*monze ai", "  *ai summary", "*", "", "   ", "!help", "xin chào *ai", "＊ai");
        var prefix = Gen.OneOfConst("*", "!", "", "monze ");
        PropertyRun.Run(
            "G10b",
            Gen.Select(age, windows, Gen.Bool, Gen.Int[0, 3], text, prefix),
            value =>
            {
                var (ageTicks, windowTicks, hasTimestamp, role, message, commandPrefix) = value;
                DateTimeOffset? created = hasTimestamp ? now - TimeSpan.FromTicks(ageTicks) : null;
                var window = TimeSpan.FromTicks(windowTicks);
                var expectedInside = created is not null && window >= TimeSpan.Zero && ageTicks >= 0 && ageTicks <= windowTicks;
                var inside = AiReplyWindow.Contains(created, now, window);

                // role: 0 = the replied (anchor) message, 1 = bot output, 2 = member message, 3 = message of a bot when botId is unknown.
                const long anchor = 10;
                const long bot = 99;
                var messageId = role == 0 ? anchor : 11;
                var sender = role is 1 or 3 ? bot : 5;
                var botId = role == 3 ? 0 : bot;
                var expectedConversation = role == 0
                    || (role != 1 && (commandPrefix.Length == 0 || !message.TrimStart().StartsWith(commandPrefix, StringComparison.Ordinal)));
                var conversation = AiReplyWindow.IsConversationMessage(messageId, anchor, sender, botId, message, commandPrefix);
                var tags = new Dictionary<string, string>
                {
                    ["window"] = expectedInside ? "inside" : "outside",
                    ["role"] = role switch { 0 => "anchor", 1 => "bot", 2 => "member", _ => "unknown-bot" },
                    ["conversation"] = expectedConversation ? "yes" : "no"
                };
                return PropertyResult.Check(
                    inside == expectedInside && conversation == expectedConversation,
                    $"age={ageTicks} window={windowTicks} role={role} text='{message}' prefix='{commandPrefix}'",
                    tags,
                    () => $"window expected {expectedInside} got {inside}; conversation expected {expectedConversation} got {conversation}");
            },
            iterations: 50_000,
            declare: static ledger => ledger
                .Dimension("window", "inside", "outside")
                .Dimension("role", "anchor", "bot", "member", "unknown-bot")
                .Dimension("conversation", "yes", "no")
                .Infeasible("role", "anchor", "conversation", "no")
                .Infeasible("role", "bot", "conversation", "yes"));
    }
}
