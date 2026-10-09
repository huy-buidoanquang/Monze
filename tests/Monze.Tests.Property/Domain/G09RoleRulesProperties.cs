using CsCheck;
using Monze.Domain;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Domain;

/// <summary>
/// G09: RoleRules. Rule kinds parse only from join/on_join/tenure (trimmed,
/// ASCII case-insensitive, independent of host culture) and round-trip through
/// their storage names; tenure matches exactly when the elapsed time reaches
/// the minimum.
/// </summary>
public sealed class G09RoleRulesProperties
{
    private static readonly Gen<string> KindText = Gen.OneOf(
        Gen.OneOfConst("join", "on_join", "tenure", "JOIN", "On_Join", "TENURE", " join ", "\ttenure\n"),
        Gen.OneOfConst("joın", "JOİN", "tenurе", "on-join", "onjoin", "tenures", "", " ", "role", "ön_join"),
        Gen.String[Gen.Char["abcdefghijklmnopqrstuvwxyz_ İı"], 0, 10]);

    [Fact]
    [Req("REQ-ROLE-001")]
    public void Rule_kinds_parse_only_from_documented_names()
    {
        PropertyRun.Run(
            "G09",
            Gen.Select(KindText, Cultures.Gen),
            static value =>
            {
                var (text, culture) = value;
                var normalized = text.Trim();
                RoleRuleKind? expected = normalized.Equals("join", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("on_join", StringComparison.OrdinalIgnoreCase)
                        ? RoleRuleKind.OnJoin
                        : normalized.Equals("tenure", StringComparison.OrdinalIgnoreCase) ? RoleRuleKind.Tenure : null;
                var parsed = Cultures.Run(culture, () => RoleRules.TryParseKind(text, out var kind) ? kind : (RoleRuleKind?)null);
                var tags = new Dictionary<string, string> { ["result"] = expected?.ToString() ?? "reject", ["culture"] = culture };
                var roundTrip = parsed is not { } accepted
                    || (RoleRules.TryParseKind(RoleRules.ToStorageName(accepted), out var again) && again == accepted);
                return PropertyResult.Check(parsed == expected && roundTrip, $"'{text}' [{culture}]", tags, () => $"expected {expected?.ToString() ?? "reject"}, got {parsed?.ToString() ?? "reject"}");
            },
            iterations: 20_000,
            declare: static ledger => ledger.Dimension("result", "OnJoin", "Tenure", "reject").Dimension("culture", Cultures.Names));
    }

    [Fact]
    [Req("REQ-ROLE-001", "REQ-ROLE-002")]
    public void Tenure_matches_once_elapsed_time_reaches_the_minimum()
    {
        var instants = Gen.Long[DateTimeOffset.MinValue.UtcTicks / 2, DateTimeOffset.MaxValue.UtcTicks / 2]
            .Select(static ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
        var minimum = Gen.OneOf(
            Gen.Int[0, 400].Select(static days => TimeSpan.FromDays(days)),
            Gen.Long[-TimeSpan.TicksPerDay, TimeSpan.TicksPerDay].Select(static ticks => TimeSpan.FromTicks(ticks)));
        var boundary = Gen.Select(instants, Gen.Int[0, 400], Gen.Long[-2, 2], static (joined, days, delta) =>
            (joined, now: joined + TimeSpan.FromDays(days) + TimeSpan.FromTicks(delta), minimum: TimeSpan.FromDays(days), shape: "boundary"));
        var free = Gen.Select(instants, instants, minimum, static (joined, now, min) => (joined, now, minimum: min, shape: "random"));
        PropertyRun.Run(
            "G09b",
            Gen.OneOf(boundary, free),
            static value =>
            {
                var (joined, now, min, shape) = value;
                var expected = (now - joined) >= min;
                var actual = RoleRules.MatchesTenure(joined, now, min);
                var later = RoleRules.MatchesTenure(joined, now + TimeSpan.FromDays(1), min);
                var tags = new Dictionary<string, string> { ["shape"] = shape, ["expected"] = expected ? "match" : "no-match" };
                return PropertyResult.Check(actual == expected && (!actual || later), $"{shape} elapsed={now - joined} min={min}", tags, () => $"expected {expected}, got {actual}, later {later}");
            },
            iterations: 20_000,
            declare: static ledger => ledger.Dimension("shape", "boundary", "random").Dimension("expected", "match", "no-match"));
    }
}
