using CsCheck;
using Monze.Domain;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Domain;

/// <summary>
/// G12: OutboxPolicy. A row with an external message id is never sent again;
/// a row is sent automatically while it has failed fewer than
/// OutboxPolicy.MaxAttempts times and held for an admin after that. Only
/// failures that prove the message never reached the channel count as
/// attempts; uncertain deliveries are reconciled against the channel before
/// any resend. OutboxAction.RetryOnce is never returned.
/// </summary>
public sealed class G12OutboxPolicyProperties
{
    private static readonly Gen<int> Attempts = Gen.OneOf(
        Gen.OneOfConst(int.MinValue, -1, 0, 1, OutboxPolicy.MaxAttempts - 1, OutboxPolicy.MaxAttempts, OutboxPolicy.MaxAttempts + 1, int.MaxValue),
        Gen.Int[0, 1_000]);

    [Fact]
    [Req("REQ-OUT-001")]
    [Covers("port:IOutboxRepository.CompleteOutboxAsync")]
    public void Delivery_is_retried_until_the_attempt_limit()
    {
        PropertyRun.Run(
            "G12",
            Gen.Select(Gen.Enum<OutboxKind>(), Gen.Bool, Attempts),
            static value =>
            {
                var (kind, delivered, attempts) = value;
                var expected = delivered
                    ? OutboxAction.AlreadyDelivered
                    : attempts < OutboxPolicy.MaxAttempts ? OutboxAction.Send : OutboxAction.HoldForAdmin;
                var actual = OutboxPolicy.Decide(kind, delivered, attempts);
                var tags = new Dictionary<string, string>
                {
                    ["kind"] = kind.ToString(),
                    ["delivered"] = delivered ? "yes" : "no",
                    ["attempts"] = attempts switch { 0 => "zero", < 0 => "negative", < OutboxPolicy.MaxAttempts => "below-limit", _ => "at-limit" }
                };
                return PropertyResult.Check(
                    actual == expected && actual != OutboxAction.RetryOnce,
                    $"{kind} delivered={delivered} attempts={attempts}",
                    tags,
                    () => $"expected {expected}, got {actual}");
            },
            iterations: 10_000,
            declare: static ledger => ledger
                .Dimension("kind", Enum.GetNames<OutboxKind>())
                .Dimension("delivered", "yes", "no")
                .Dimension("attempts", "zero", "negative", "below-limit", "at-limit"));
    }
}
