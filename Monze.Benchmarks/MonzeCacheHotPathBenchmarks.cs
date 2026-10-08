using BenchmarkDotNet.Attributes;
using Monze.Application;
using Monze.Infrastructure.Caching;

[MemoryDiagnoser]
[ShortRunJob]
public class MonzeCacheHotPathBenchmarks
{
    private MonzeL1Cache _cache = null!;
    private MonzeCacheKey _key;
    private ReadModelCacheEntry _expected;

    [GlobalSetup]
    public void Setup()
    {
        _cache = new MonzeL1Cache();
        _key = new MonzeCacheKey(2104288434238525440, "clan-settings", "current");
        _expected = new ReadModelCacheEntry(1, "{}");
        _cache.Set(
            _key,
            _expected,
            TimeSpan.FromMinutes(5),
            2);
    }

    [Benchmark]
    [ZeroAllocationGate]
    public bool L1TypedKeyHit()
        => _cache.TryGet(_key, out ReadModelCacheEntry value)
            && value.Version == _expected.Version;
}
