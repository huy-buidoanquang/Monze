namespace Monze.Domain;

public static class ClanDiscovery
{
    public const int AssumedClanCap = 100;

    public static bool ListLooksCapped(int returnedCount, int cap = AssumedClanCap) => returnedCount >= cap;

    public static bool ListLooksIncomplete(
        int returnedCount,
        int knownCount,
        int cap = AssumedClanCap)
        => ListLooksCapped(returnedCount, cap)
            || (knownCount > 0 && returnedCount == 0);

    public static IReadOnlyList<(ClanScanItem Item, ClanScanDisposition Disposition)> Merge(
        IReadOnlyList<KnownClan> known,
        IReadOnlyList<ClanScanItem> listed)
    {
        var knownById = new Dictionary<long, KnownClan>(known.Count);
        for (var i = 0; i < known.Count; i++)
        {
            knownById[known[i].ClanId] = known[i];
        }

        // Every listed clan has the bot as a member, so it is registered even
        // when the list looks capped (DEF-06: mezon-api lists at most 100 clans);
        // a capped list only stops IsKnownMissing from retiring unlisted ones.
        var results = new List<(ClanScanItem, ClanScanDisposition)>(listed.Count);
        foreach (var item in listed)
        {
            if (!knownById.TryGetValue(item.ClanId, out var existing))
            {
                results.Add((item, ClanScanDisposition.Inserted));
                continue;
            }

            results.Add((item, existing.OwnerId != item.OwnerId
                ? ClanScanDisposition.OwnerReplaced
                : ClanScanDisposition.Unchanged));
        }

        return results;
    }

    public static bool IsKnownMissing(
        KnownClan clan,
        IReadOnlySet<long> listedClanIds,
        bool listLooksCapped)
        => !listLooksCapped && !listedClanIds.Contains(clan.ClanId);

    public static IReadOnlyList<KnownClan> PendingJoins(
        IReadOnlyList<KnownClan> clans,
        IReadOnlySet<long> successfullyJoinedClanIds)
    {
        var pending = new List<KnownClan>();
        for (var i = 0; i < clans.Count; i++)
        {
            if (!successfullyJoinedClanIds.Contains(clans[i].ClanId))
            {
                pending.Add(clans[i]);
            }
        }

        return pending;
    }
}
