using CsCheck;
using Monze.Application;
using Monze.Infrastructure.Caching;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Ui;

/// <summary>
/// G26: welcome draft stores. A draft token lives 10 minutes, is scoped to
/// its clan and channel, and only one claim wins until it is released or
/// completed; a setup draft lives 15 minutes per (clan, channel, user).
/// G27: MonzeL1Cache never serves an expired entry, never replaces a newer
/// version with an older one (an expired newer entry keeps blocking until a
/// read or the 30 s maintenance pass removes it) and ignores entries outside
/// 1 B..64 KiB.
/// G28: MonzeCacheKey equality and hashing are ordinal and agree.
/// </summary>
public sealed class G26G27G28CacheAndDraftProperties
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] DraftOperations = ["get", "claim", "release", "complete", "advance"];

    [Fact]
    [Req("REQ-WEL-002")]
    [Covers("port:IWelcomeDraftStore.TryClaim")]
    public void Draft_tokens_expire_stay_scoped_and_admit_one_claim()
    {
        var operations = Gen.Select(Gen.OneOfConst(DraftOperations), Gen.Int[0, 2], Gen.Int[0, 12]).Array[1, 25];
        PropertyRun.Run(
            "G26",
            operations,
            static ops =>
            {
                var store = new MemoryWelcomeDraftStore();
                var now = Start;
                var ticket = store.Create(10, 20, true, new WelcomeEmbedSettings(Title: "x"), now);
                var claimed = false;
                var removed = false;
                var tags = new Dictionary<string, string> { ["first"] = ops[0].Item1, ["last"] = ops[^1].Item1 };
                var input = string.Join(" ", ops.Select(static op => $"{op.Item1}:{op.Item2}:{op.Item3}"));
                foreach (var (operation, scope, minutes) in ops)
                {
                    var (clan, channel) = scope switch { 0 => (10L, 20L), 1 => (11L, 20L), _ => (10L, 21L) };
                    var alive = !removed && now < ticket.ExpiresAt;
                    switch (operation)
                    {
                        case "get":
                            var got = store.TryGet(ticket.Token, clan, channel, now, out _);
                            if (got != (alive && scope == 0))
                            {
                                return PropertyResult.Fail(input, tags, $"get expected {alive && scope == 0}");
                            }

                            removed |= scope == 0 && !alive;
                            break;
                        case "claim":
                            var won = store.TryClaim(ticket.Token, clan, channel, now, out _);
                            var expectedWin = alive && scope == 0 && !claimed;
                            if (won != expectedWin)
                            {
                                return PropertyResult.Fail(input, tags, $"claim expected {expectedWin}");
                            }

                            claimed |= won;
                            removed |= scope == 0 && !alive;
                            break;
                        case "release":
                            store.Release(ticket);
                            claimed = false;
                            break;
                        case "complete":
                            store.Complete(ticket);
                            removed = true;
                            claimed = false;
                            break;
                        default:
                            now = now.AddMinutes(minutes);
                            break;
                    }
                }

                return PropertyResult.Pass(input, tags);
            },
            iterations: 30_000,
            declare: static ledger => ledger.Dimension("first", DraftOperations).Dimension("last", DraftOperations));
    }

    [Fact]
    [Req("REQ-WEL-002")]
    public async Task Concurrent_claims_on_one_token_admit_exactly_one()
    {
        var rounds = 2_000 * CampaignEnvironment.PbtScale;
        using var ledger = CaseLedger.Open("property", "G26b");
        for (var round = 0; round < rounds; round++)
        {
            var store = new MemoryWelcomeDraftStore();
            var ticket = store.Create(10, 20, true, new WelcomeEmbedSettings(Title: "x"), Start);
            var winners = 0;
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                if (store.TryClaim(ticket.Token, 10, 20, Start.AddMinutes(1), out var claimedTicket))
                {
                    Interlocked.Increment(ref winners);
                }
            })));
            ledger.Record(winners == 1 ? "pass" : "fail", $"round {round}: {winners} winners");
            Assert.Equal(1, winners);
        }
    }

    [Fact]
    [Req("REQ-WEL-002")]
    [Covers("port:IWelcomeSetupDraftStore.TryGet")]
    public void Setup_drafts_live_fifteen_minutes_per_user()
    {
        PropertyRun.Run(
            "G26c",
            Gen.Select(Gen.Int[0, 30].Array[1, 6], Gen.Int[0, 2]),
            static value =>
            {
                var (steps, reader) = value;
                var store = new MemoryWelcomeSetupDraftStore();
                var owner = new WelcomeSetupDraftKey(10, 20, 30);
                var other = reader switch { 0 => owner, 1 => new WelcomeSetupDraftKey(10, 20, 31), _ => new WelcomeSetupDraftKey(10, 21, 30) };
                var settings = new WelcomeSettings(true, "Chào", 3);
                store.Set(owner, settings, Start);
                var elapsed = 0;
                foreach (var step in steps)
                {
                    elapsed += step;
                    var found = store.TryGet(other, Start.AddMinutes(elapsed), out var read);
                    var expected = reader == 0 && elapsed < 15;
                    var tags = new Dictionary<string, string> { ["reader"] = reader == 0 ? "owner" : "other", ["alive"] = elapsed < 15 ? "yes" : "no" };
                    if (found != expected || (found && !ReferenceEquals(read, settings)))
                    {
                        return PropertyResult.Fail($"elapsed={elapsed} reader={reader}", tags, $"found={found} expected={expected}");
                    }
                }

                var lastTags = new Dictionary<string, string> { ["reader"] = reader == 0 ? "owner" : "other", ["alive"] = elapsed < 15 ? "yes" : "no" };
                return PropertyResult.Pass($"steps={string.Join(",", steps)} reader={reader}", lastTags);
            },
            iterations: 20_000,
            declare: static ledger => ledger.Dimension("reader", "owner", "other").Dimension("alive", "yes", "no"));
    }

    [Fact]
    [Req("REQ-CACHE-001")]
    [Covers("port:IReadModelCache.GetAsync")]
    public void L1_cache_follows_version_ttl_and_size_rules()
    {
        var operations = Gen.Select(
            Gen.OneOfConst("set", "get", "remove", "advance"),
            Gen.Int[0, 2],
            Gen.Long[0, 5],
            Gen.OneOfConst(0, 1, 100, 65_536, 65_537),
            Gen.Int[0, 120]).Array[1, 30];
        PropertyRun.Run(
            "G27",
            operations,
            static ops =>
            {
                var time = new ManualTimeProvider(Start);
                using var cache = new MonzeL1Cache(time);
                var model = new Dictionary<int, (long Version, string Payload, TimeSpan Expires)>();
                var elapsed = TimeSpan.Zero;
                var nextMaintenance = TimeSpan.FromSeconds(30);
                var input = string.Join(" ", ops.Select(static op => $"{op.Item1}:{op.Item2}:{op.Item3}:{op.Item4}:{op.Item5}"));
                foreach (var (operation, slot, version, size, seconds) in ops)
                {
                    var key = new MonzeCacheKey(slot == 2 ? 11 : 10, "settings", slot == 1 ? "b" : "a");
                    switch (operation)
                    {
                        case "set":
                            var payload = $"v{version}";
                            cache.Set(key, new ReadModelCacheEntry(version, payload), TimeSpan.FromSeconds(seconds), size);
                            if (size is > 0 and <= 65_536)
                            {
                                if (!model.TryGetValue(slot, out var current) || current.Version <= version)
                                {
                                    model[slot] = (version, payload, elapsed + TimeSpan.FromTicks(Math.Max(1, TimeSpan.FromSeconds(seconds).Ticks)));
                                }
                            }

                            break;
                        case "get":
                            var hit = cache.TryGet(key, out var entry);
                            var expectedHit = model.TryGetValue(slot, out var expected) && expected.Expires > elapsed;
                            if (hit != expectedHit || (hit && (entry.Version != expected.Version || entry.Payload != expected.Payload)))
                            {
                                return PropertyResult.Fail(input, new Dictionary<string, string>(), $"get slot {slot}: expected {(expectedHit ? expected.Payload : "miss")}, got {(hit ? entry.Payload : "miss")}");
                            }

                            if (!expectedHit)
                            {
                                model.Remove(slot);
                            }

                            break;
                        case "remove":
                            cache.Remove(key);
                            model.Remove(slot);
                            break;
                        default:
                            time.Advance(TimeSpan.FromSeconds(seconds));
                            elapsed += TimeSpan.FromSeconds(seconds);
                            if (elapsed >= nextMaintenance)
                            {
                                foreach (var stale in model.Where(pair => pair.Value.Expires <= elapsed).Select(static pair => pair.Key).ToList())
                                {
                                    model.Remove(stale);
                                }

                                while (nextMaintenance <= elapsed)
                                {
                                    nextMaintenance += TimeSpan.FromSeconds(30);
                                }
                            }

                            break;
                    }
                }

                return PropertyResult.Pass(input, new Dictionary<string, string> { ["ops"] = ops.Length < 10 ? "short" : "long" });
            },
            iterations: 30_000,
            declare: static ledger => ledger.Dimension("ops", "short", "long"));
    }

    [Fact]
    [Req("REQ-CACHE-001")]
    public void Cache_keys_compare_and_hash_ordinally()
    {
        var part = Gen.OneOfConst("settings", "Settings", "SETTINGS", "ı", "I", "İ", "á", "á", "", " ");
        PropertyRun.Run(
            "G28",
            Gen.Select(Gen.Long[0, 2], part, part, Gen.Long[0, 2], part, part),
            static value =>
            {
                var (clanA, kindA, idA, clanB, kindB, idB) = value;
                var a = new MonzeCacheKey(clanA, kindA, idA);
                var b = new MonzeCacheKey(clanB, kindB, idB);
                var expected = clanA == clanB && string.Equals(kindA, kindB, StringComparison.Ordinal) && string.Equals(idA, idB, StringComparison.Ordinal);
                var tags = new Dictionary<string, string> { ["equal"] = expected ? "yes" : "no" };
                var ok = a.Equals(b) == expected && a.Equals((object)b) == expected && (!expected || a.GetHashCode() == b.GetHashCode());
                return PropertyResult.Check(ok, $"({clanA},'{kindA}','{idA}') vs ({clanB},'{kindB}','{idB}')", tags, static () => "equality or hash disagree");
            },
            iterations: 20_000,
            declare: static ledger => ledger.Dimension("equal", "yes", "no"));
    }
}
