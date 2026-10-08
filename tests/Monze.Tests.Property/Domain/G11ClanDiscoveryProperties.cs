using CsCheck;
using Monze.Domain;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Domain;

/// <summary>
/// G11: ClanDiscovery around the assumed list cap of 100. Every listed clan
/// gets exactly one disposition and an unknown one is inserted, capped list
/// or not (DEF-06); a capped list never marks a known clan missing; pending
/// joins keep the input order.
/// </summary>
public sealed class G11ClanDiscoveryProperties
{
    private static readonly string[] ListedSizes = ["0", "1-98", "99", "100", "101-150"];
    private static readonly string[] KnownSizes = ["0", "1-50", "51-150"];

    private static readonly Gen<DiscoveryCase> Cases =
        from listedSize in Gen.OneOfConst(ListedSizes)
        from knownSize in Gen.OneOfConst(KnownSizes)
        from listedCount in listedSize switch
        {
            "0" => Gen.Const(0),
            "1-98" => Gen.Int[1, 98],
            "99" => Gen.Const(99),
            "100" => Gen.Const(100),
            _ => Gen.Int[101, 150]
        }
        from knownCount in knownSize switch
        {
            "0" => Gen.Const(0),
            "1-50" => Gen.Int[1, 50],
            _ => Gen.Int[51, 150]
        }
        from listedIds in Gen.Long[1, 300].ArrayUnique[listedCount, listedCount]
        from knownIds in Gen.Long[1, 300].ArrayUnique[knownCount, knownCount]
        from owners in Gen.Long[1, 3].Array[listedCount + knownCount, listedCount + knownCount]
        from joinedMask in Gen.Bool.Array[knownCount, knownCount]
        select new DiscoveryCase(listedSize, knownSize,
            knownIds.Select((id, i) => new KnownClan(id, owners[listedCount + i])).ToArray(),
            listedIds.Select((id, i) => new ClanScanItem(id, owners[i])).ToArray(),
            joinedMask);

    [Fact]
    [Req("REQ-CONN-001")]
    [Covers("port:IClanRegistryRepository.ApplyClanScanAsync")]
    public void Discovery_never_infers_absence_from_a_capped_list()
    {
        PropertyRun.Run(
            "G11",
            Cases,
            static item =>
            {
                var tags = new Dictionary<string, string> { ["listed"] = item.ListedSize, ["known"] = item.KnownSize };
                var input = $"listed={item.Listed.Length} known={item.Known.Length}";
                var capped = ClanDiscovery.ListLooksCapped(item.Listed.Length);
                if (capped != (item.Listed.Length >= ClanDiscovery.AssumedClanCap))
                {
                    return PropertyResult.Fail(input, tags, "capped flag");
                }

                if (ClanDiscovery.ListLooksIncomplete(item.Listed.Length, item.Known.Length) != (capped || (item.Known.Length > 0 && item.Listed.Length == 0)))
                {
                    return PropertyResult.Fail(input, tags, "incomplete flag");
                }

                var known = item.Known.ToDictionary(static clan => clan.ClanId);
                var merged = ClanDiscovery.Merge(item.Known, item.Listed);
                if (merged.Count != item.Listed.Length)
                {
                    return PropertyResult.Fail(input, tags, "merge must return one disposition per listed clan");
                }

                for (var i = 0; i < merged.Count; i++)
                {
                    var (listed, disposition) = merged[i];
                    var expected = !known.TryGetValue(listed.ClanId, out var existing)
                        ? ClanScanDisposition.Inserted
                        : existing.OwnerId != listed.OwnerId ? ClanScanDisposition.OwnerReplaced : ClanScanDisposition.Unchanged;
                    if (!ReferenceEquals(listed, item.Listed[i]) || disposition != expected)
                    {
                        return PropertyResult.Fail(input, tags, $"clan #{i}: expected {expected}, got {disposition}");
                    }
                }

                var listedIds = item.Listed.Select(static clan => clan.ClanId).ToHashSet();
                foreach (var clan in item.Known)
                {
                    if (ClanDiscovery.IsKnownMissing(clan, listedIds, capped) != (!capped && !listedIds.Contains(clan.ClanId)))
                    {
                        return PropertyResult.Fail(input, tags, "known-missing");
                    }
                }

                var joined = item.Known.Where((_, i) => item.Joined[i]).Select(static clan => clan.ClanId).ToHashSet();
                var pending = ClanDiscovery.PendingJoins(item.Known, joined);
                var expectedPending = item.Known.Where(clan => !joined.Contains(clan.ClanId)).ToArray();
                return PropertyResult.Check(pending.SequenceEqual(expectedPending), input, tags, static () => "pending joins");
            },
            iterations: 30_000,
            declare: static ledger => ledger.Dimension("listed", ListedSizes).Dimension("known", KnownSizes));
    }

    public sealed record DiscoveryCase(string ListedSize, string KnownSize, KnownClan[] Known, ClanScanItem[] Listed, bool[] Joined);
}
