using BenchmarkDotNet.Attributes;
using Monze.Application.Commands;

/// <summary>
/// Command rate limiter beyond the known-key hot path: a key over its limit,
/// and a new key arriving while the table is full of live windows. The table
/// only trims when full, one expired entry per call, so that second case scans
/// every entry under the lock and then rejects the key (CAND-06).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class RateLimiterBenchmarks
{
    private const int MaxEntries = 1024;

    private MonzeCommandRateLimiter _limited = null!;
    private MonzeCommandRateLimiter _rotating = null!;
    private long _nextUser;

    [GlobalSetup]
    public void Setup()
    {
        _limited = new MonzeCommandRateLimiter(Options(userLimit: 1), FrozenTimeProvider.Instance);
        _limited.TryAcquire(206, 1001, MonzeCommandNames.Role, out _);
        _rotating = new MonzeCommandRateLimiter(Options(userLimit: 1_000_000), FrozenTimeProvider.Instance);
        _nextUser = 0;
    }

    [Benchmark]
    [ZeroAllocationGate]
    public bool KnownKeyOverLimit()
        => _limited.TryAcquire(206, 1001, MonzeCommandNames.Role, out _);

    [Benchmark]
    public bool NewKeyRejectedAtEntryBound()
        => _rotating.TryAcquire(206, ++_nextUser, MonzeCommandNames.Role, out _);

    private static MonzeRateLimitOptions Options(int userLimit)
        => new(
            UserLimit: userLimit,
            UserWindow: TimeSpan.FromMinutes(1),
            AiLimit: userLimit,
            AiWindow: TimeSpan.FromMinutes(1),
            MeetingLimit: userLimit,
            MeetingWindow: TimeSpan.FromMinutes(1),
            AdminLimit: userLimit,
            AdminWindow: TimeSpan.FromMinutes(1),
            MaxEntries: MaxEntries);
}
