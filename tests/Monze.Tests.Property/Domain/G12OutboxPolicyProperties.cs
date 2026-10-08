using CsCheck;
using Monze.Domain;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Domain;

/// <summary>
/// G12: OutboxPolicy. A row with an external message id is never sent again;
/// only a row that has never failed is sent automatically; anything else is
/// held for an admin. Together with the SQL lease (claim pending or expired
/// sending rows, attempts + 1 on completion) this means automatic delivery is
/// attempted once. OutboxAction.RetryOnce is never returned.
/// </summary>
public sealed class G12OutboxPolicyProperties
{
    private static readonly Gen<int> Attempts = Gen.OneOf(
        Gen.OneOfConst(int.MinValue, -1, 0, 1, 2, 5, int.MaxValue),
        Gen.Int[0, 1_000]);

    [Fact]
    [Req("REQ-OUT-001")]
    [Covers("port:IOutboxRepository.CompleteOutboxAsync")]
    public void Delivery_is_attempted_automatically_at_most_once()
    {
        PropertyRun.Run(
            "G12",
            Gen.Select(Gen.Enum<OutboxKind>(), Gen.Bool, Attempts),
            static value =>
            {
                var (kind, delivered, attempts) = value;
                var expected = delivered
                    ? OutboxAction.AlreadyDelivered
                    : attempts == 0 ? OutboxAction.Send : OutboxAction.HoldForAdmin;
                var actual = OutboxPolicy.Decide(kind, delivered, attempts);
                var tags = new Dictionary<string, string>
                {
                    ["kind"] = kind.ToString(),
                    ["delivered"] = delivered ? "yes" : "no",
                    ["attempts"] = attempts switch { 0 => "zero", < 0 => "negative", _ => "positive" }
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
                .Dimension("attempts", "zero", "negative", "positive"));
    }
}
