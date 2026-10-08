using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;

namespace Monze.Tests.E2E.Harness;

/// <summary>
/// A clock for Monze's process-memory policy cache (the private-interaction
/// bindings live there for 30 minutes) that a test can move forward, so
/// "after the binding expired" runs in milliseconds.
/// </summary>
internal sealed class ManualCacheClock : ISystemClock
{
    private long _offsetTicks;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    /// <summary>Replaces Monze's IMemoryCache with one on this clock (same size limit as production).</summary>
    public void Install(IServiceCollection services)
        => services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 64 * 1024 * 1024,
            Clock = this
        }));
}
