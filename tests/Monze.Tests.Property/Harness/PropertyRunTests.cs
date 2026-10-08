using CsCheck;
using Monze.Testing;
using Xunit;
using Xunit.Sdk;

namespace Monze.Tests.Property.Harness;

public sealed class PropertyRunTests
{
    private static readonly Gen<int[]> Values = Gen.Int[-1_000, 1_000].Array[0, 20];

    [Fact]
    [Req("REQ-PBT-001")]
    public void A_case_seed_regenerates_the_same_input()
    {
        var master = new PCG(7, 42);
        for (var i = 0; i < 1_000; i++)
        {
            var casePcg = new PCG(7, master.Next64());
            var seed = casePcg.ToString();
            var original = Values.Generate(casePcg, null, out _);

            var replayed = Values.Generate(PCG.Parse(seed), null, out _);

            Assert.Equal(original, replayed);
        }
    }

    [Fact]
    [Req("REQ-PBT-001")]
    public void A_failing_property_reports_the_input_seed_and_a_shrunk_counterexample()
    {
        var error = Assert.Throws<XunitException>(() => PropertyRun.Run(
            "harness-failing",
            Values,
            static values => PropertyResult.Check(values.Sum() < 2_000, $"{values.Length} values", new Dictionary<string, string>(), static () => "sum too large"),
            iterations: 2_000,
            recordToCampaign: false));

        Assert.Contains("checks failed", error.Message, StringComparison.Ordinal);
        Assert.Contains("Seed: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("Shrink:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Req("REQ-PBT-001")]
    public void Missing_pairwise_combinations_fail_the_property()
    {
        var error = Assert.Throws<XunitException>(() => PropertyRun.Run(
            "harness-pairwise",
            Gen.OneOfConst("a", "b"),
            static value => PropertyResult.Pass(value, new Dictionary<string, string> { ["letter"] = value, ["digit"] = "1" }),
            iterations: 200,
            declare: static ledger => ledger.Dimension("letter", "a", "b").Dimension("digit", "1", "2"),
            recordToCampaign: false));

        Assert.Contains("pairwise coverage 2/4", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Req("REQ-PBT-001")]
    public void A_known_defect_that_no_longer_reproduces_is_an_xpass()
    {
        Assert.Throws<XPassException>(() => PropertyRun.Run(
            "harness-xpass",
            Gen.Int[0, 10],
            static value => PropertyResult.Pass(value.ToString(System.Globalization.CultureInfo.InvariantCulture), new Dictionary<string, string>(), "CAND-TEST"),
            iterations: 100,
            knownDefects: ["CAND-TEST"],
            recordToCampaign: false));
    }
}
