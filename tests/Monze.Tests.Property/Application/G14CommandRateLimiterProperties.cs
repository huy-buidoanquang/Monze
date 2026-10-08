using CsCheck;
using Monze.Application.Commands;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Application;

/// <summary>
/// G14: MonzeCommandRateLimiter against a model of fixed windows per
/// (clan, user, bucket, command) measured in elapsed time, with the documented
/// bounded table (when full, one expired window is trimmed, otherwise a new key
/// is rejected). Sequences mix real elapsed time with wall-clock jumps, which
/// must change nothing (regression for DEF-10: windows used to be measured on
/// the wall clock, so a backward jump extended a lockout and a forward jump
/// ended a window early).
/// G14b: concurrent callers on one key never get more than the limit.
/// </summary>
public sealed class G14CommandRateLimiterProperties
{
    private static readonly string[] Commands = ["help", "ai", "Translate", "meeting", " summary ", "setup", "AVA", "unknown-x", "role"];
    private static readonly string[] Clocks = ["monotonic", "forward-jump", "backward-jump"];

    private static readonly Gen<Operation> Operations =
        from clan in Gen.Long[1, 2]
        from user in Gen.Long[1, 4]
        from command in Gen.OneOfConst(Commands)
        from elapsedMillis in Gen.Frequency((6, Gen.Int[0, 2_000]), (2, Gen.Int[2_000, 40_000]), (1, Gen.Const(0)))
        from jump in Gen.Frequency((20, Gen.Const(0)), (1, Gen.Int[-7_200, -1]), (1, Gen.Int[1, 7_200]))
        select new Operation(clan, user, command, TimeSpan.FromMilliseconds(elapsedMillis), TimeSpan.FromSeconds(jump));

    private static readonly Gen<LimiterCase> Cases =
        from limit in Gen.Int[1, 3]
        from windowSeconds in Gen.Int[1, 30]
        from maxEntries in Gen.Int[2, 12]
        from operations in Operations.Array[1, 60]
        select new LimiterCase(limit, TimeSpan.FromSeconds(windowSeconds), maxEntries, operations);

    [Fact]
    [Req("REQ-RL-001")]
    [Covers("msg:RateLimited")]
    public void Limiter_matches_the_window_model()
    {
        PropertyRun.Run(
            "G14",
            Cases,
            static item =>
            {
                var options = new MonzeRateLimitOptions(
                    item.Limit, item.Window, item.Limit, item.Window, item.Limit, item.Window, item.Limit, item.Window, item.MaxEntries);
                var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
                var limiter = new MonzeCommandRateLimiter(options, time);
                var model = new Dictionary<(long, long, string), (TimeSpan Expires, int Count)>();
                var elapsed = TimeSpan.Zero;
                var backward = false;
                var forward = false;
                for (var i = 0; i < item.Operations.Length; i++)
                {
                    var operation = item.Operations[i];
                    elapsed += operation.Elapsed;
                    time.Advance(operation.Elapsed);
                    time.JumpWallClock(operation.Jump);
                    backward |= operation.Jump < TimeSpan.Zero;
                    forward |= operation.Jump > TimeSpan.Zero;
                    var key = (operation.Clan, operation.User, Bucket(operation.Command));
                    var expected = Model(model, key, elapsed, item, out var expectedRetry);
                    var actual = limiter.TryAcquire(operation.Clan, operation.User, operation.Command, out var retry);
                    if (actual != expected || (!actual && retry != expectedRetry))
                    {
                        var clock = backward ? "backward-jump" : forward ? "forward-jump" : "monotonic";
                        var note = $"op {i} {operation.Command}: expected {(expected ? "allow" : $"deny {expectedRetry}")}, got {(actual ? "allow" : $"deny {retry}")}";
                        return PropertyResult.Fail(item.Describe(), Tags(item, clock), note);
                    }
                }

                var clockTag = backward ? "backward-jump" : forward ? "forward-jump" : "monotonic";
                return PropertyResult.Pass(item.Describe(), Tags(item, clockTag));
            },
            iterations: 4_000,
            declare: static ledger => ledger.Dimension("clock", Clocks).Dimension("pressure", "bounded", "roomy"));
    }

    [Fact]
    [Req("REQ-RL-001")]
    public async Task Concurrent_callers_never_exceed_the_limit()
    {
        var rounds = 300 * CampaignEnvironment.PbtScale;
        using var ledger = CaseLedger.Open("property", "G14b");
        var random = RunSeed.RandomFor("G14b");
        for (var round = 0; round < rounds; round++)
        {
            var limit = random.Next(1, 40);
            var limiter = new MonzeCommandRateLimiter(new MonzeRateLimitOptions(
                limit, TimeSpan.FromMinutes(1), limit, TimeSpan.FromMinutes(1), limit, TimeSpan.FromMinutes(1), limit, TimeSpan.FromMinutes(1), 1024));
            var allowed = 0;
            const int threads = 8;
            const int attempts = 50;
            await Task.WhenAll(Enumerable.Range(0, threads).Select(worker => Task.Run(() =>
            {
                for (var i = 0; i < attempts; i++)
                {
                    if (limiter.TryAcquire(206, 1001, "help", out _))
                    {
                        Interlocked.Increment(ref allowed);
                    }
                }
            })));
            var outcome = allowed == Math.Min(limit, threads * attempts) ? "pass" : "fail";
            ledger.Record(outcome, $"limit {limit}: {allowed} allowed of {threads * attempts}");
            Assert.Equal(Math.Min(limit, threads * attempts), allowed);
        }
    }

    private static bool Model(
        Dictionary<(long, long, string), (TimeSpan Expires, int Count)> model,
        (long, long, string) key,
        TimeSpan now,
        LimiterCase item,
        out TimeSpan retry)
    {
        if (model.Count >= item.MaxEntries)
        {
            foreach (var pair in model)
            {
                if (pair.Value.Expires <= now)
                {
                    model.Remove(pair.Key);
                    break;
                }
            }
        }

        if (model.Count >= item.MaxEntries && !model.ContainsKey(key))
        {
            retry = item.Window;
            return false;
        }

        if (!model.TryGetValue(key, out var window) || now >= window.Expires)
        {
            model[key] = (now + item.Window, 1);
            retry = TimeSpan.Zero;
            return true;
        }

        if (window.Count >= item.Limit)
        {
            retry = window.Expires - now;
            return false;
        }

        model[key] = (window.Expires, window.Count + 1);
        retry = TimeSpan.Zero;
        return true;
    }

    /// <summary>Bucket and normalized command: AI actions share one window per action.</summary>
    private static string Bucket(string command)
        => MonzeCommandNames.Normalize(command.Trim()) switch
        {
            "avatar" => "avatar",
            var known when known is "help" or "ai" or "translate" or "meeting" or "summary" or "setup" or "role" => known,
            _ => "unknown"
        };

    private static Dictionary<string, string> Tags(LimiterCase item, string clock)
        => new()
        {
            ["clock"] = clock,
            ["pressure"] = item.MaxEntries <= 6 ? "bounded" : "roomy"
        };

    private sealed record Operation(long Clan, long User, string Command, TimeSpan Elapsed, TimeSpan Jump);

    private sealed record LimiterCase(int Limit, TimeSpan Window, int MaxEntries, Operation[] Operations)
    {
        public string Describe()
            => $"limit={Limit} window={Window.TotalSeconds}s max={MaxEntries} ops={string.Join(" ", Operations.Select(static op => $"{op.Clan}/{op.User}/{op.Command.Trim()}+{op.Elapsed.TotalMilliseconds}{(op.Jump != TimeSpan.Zero ? $"j{op.Jump.TotalSeconds}" : string.Empty)}"))}";
    }
}
