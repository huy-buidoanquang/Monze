using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monze.Hosting;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Hosting;

public sealed class PolicyCacheBoundTests : IDisposable
{
    // The smallest realistic footprint of one policy entry (key, boxed value, entry object).
    private const long MinimumEntryBytes = 100;
    private const long IntendedBytes = 64L * 1024 * 1024;

    private readonly string _contentRoot = Directory.CreateTempSubdirectory("monze-policy-cache-").FullName;

    public void Dispose() => Directory.Delete(_contentRoot, recursive: true);

    /// <summary>
    /// The host gives IMemoryCache SizeLimit = 64 MiB, but every entry MonzeBot
    /// stores declares Size = 1, so the limit counts entries (67,108,864 of
    /// them) instead of bounding memory to 64 MiB (DEF-05).
    /// </summary>
    [Fact]
    [Req("REQ-CACHE-001", "REQ-PERF-001")]
    public void Policy_cache_limit_bounds_memory_to_its_intended_size()
    {
        var builder = MonzeHostComposition.CreateBuilder([], new MonzeHostCompositionOptions
        {
            Settings = new HostApplicationBuilderSettings { DisableDefaults = true, ContentRootPath = _contentRoot },
            LoadAppSettingsFiles = false,
            AddProductionLogging = false,
            Configuration =
            [
                new("Monze:Postgres", "Host=127.0.0.1;Port=1;Database=monze_policy;Username=monze"),
                new("Monze:Logging:Directory", Path.Combine(_contentRoot, "logs"))
            ]
        });
        using var provider = builder.Services.BuildServiceProvider();
        var limit = provider.GetRequiredService<IOptions<MemoryCacheOptions>>().Value.SizeLimit;

        KnownDefect.ExpectFailure("DEF-05", () =>
            Assert.True(
                limit is { } entries && entries * MinimumEntryBytes <= IntendedBytes,
                $"SizeLimit {limit} counts entries of Size = 1; at {MinimumEntryBytes} B each that allows {limit * MinimumEntryBytes / (1024 * 1024)} MiB"));
    }
}
