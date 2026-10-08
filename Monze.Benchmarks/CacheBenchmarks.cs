using BenchmarkDotNet.Attributes;
using Monze.Application;
using Monze.Infrastructure.Caching;

/// <summary>L1 read-model cache paths other than the typed-key hit.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class CacheBenchmarks
{
    private MonzeL1Cache _cache = null!;
    private MonzeCacheKey _present;
    private MonzeCacheKey _absent;
    private ReadModelCacheEntry _entry;
    private long _version;

    [GlobalSetup]
    public void Setup()
    {
        _cache = new MonzeL1Cache();
        _present = new MonzeCacheKey(2104288434238525440, "clan-settings", "current");
        _absent = new MonzeCacheKey(2104288434238525440, "clan-settings", "missing");
        _entry = new ReadModelCacheEntry(1, "{}");
        _cache.Set(_present, _entry, TimeSpan.FromMinutes(5), 2);
        _version = 1;
    }

    [GlobalCleanup]
    public void Cleanup() => _cache.Dispose();

    [Benchmark]
    public bool L1Miss()
        => _cache.TryGet(_absent, out _);

    [Benchmark]
    public void L1ReplaceNewerVersion()
        => _cache.Set(_present, new ReadModelCacheEntry(++_version, "{}"), TimeSpan.FromMinutes(5), 2);

    [Benchmark]
    public int CacheKeyHash()
        => _present.GetHashCode();
}
