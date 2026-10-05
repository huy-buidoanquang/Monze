using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Monze.Application;
using StackExchange.Redis;
using System.Globalization;
using System.Diagnostics.Metrics;
using System.Text;

namespace Monze.Infrastructure.Caching;

public sealed class MonzeReadModelCache : IReadModelCache, IDisposable
{
    private static readonly Meter Meter = new("Monze.Cache", "1.0");
    private static readonly Counter<long> L1Hits = Meter.CreateCounter<long>("monze.cache.l1.hit");
    private static readonly Counter<long> L2Hits = Meter.CreateCounter<long>("monze.cache.l2.hit");
    private static readonly Counter<long> L2Misses = Meter.CreateCounter<long>("monze.cache.l2.miss");
    private static readonly Counter<long> RedisErrors = Meter.CreateCounter<long>("monze.cache.redis.error");
    private const int MaxEntrySize = 64 * 1024;
    private const string InvalidationSuffix = ":invalidate";
    private const long RedisFailureCooldownMilliseconds = 5_000;
    private readonly ISubscriber _subscriber;
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly IDatabase _database;
    private readonly MonzeL1Cache _l1 = new();
    private readonly ConcurrentDictionary<MonzeCacheKey, Lazy<Task<ReadModelCacheEntry?>>> _loads = new();
    private readonly ConcurrentDictionary<MonzeCacheKey, long> _generations = new();
    private readonly string _prefix;
    private readonly string _publisherId = Guid.NewGuid().ToString("N");
    private readonly Action<RedisChannel, RedisValue> _invalidationHandler;
    private readonly EventHandler<ConnectionFailedEventArgs> _connectionFailedHandler;
    private readonly EventHandler<ConnectionFailedEventArgs> _connectionRestoredHandler;
    private readonly ILogger<MonzeReadModelCache> _logger;
    private long _redisRetryAfterMilliseconds;
    private int _redisHealthy;

    public MonzeReadModelCache(
        IConnectionMultiplexer multiplexer,
        string environment,
        long botId,
        ILogger<MonzeReadModelCache> logger)
    {
        _multiplexer = multiplexer;
        _database = multiplexer.GetDatabase();
        _subscriber = multiplexer.GetSubscriber();
        _logger = logger;
        _prefix = $"monze:v1:{environment}:{botId}:";
        _invalidationHandler = (_, value) => InvalidateLocal(value);
        _connectionFailedHandler = (_, _) => MarkRedisFailure();
        _connectionRestoredHandler = (_, _) => _ = SubscribeToInvalidationsAsync();
        _multiplexer.ConnectionFailed += _connectionFailedHandler;
        _multiplexer.ConnectionRestored += _connectionRestoredHandler;
        _ = SubscribeToInvalidationsAsync();
    }

    public bool IsConnected
        => _multiplexer.IsConnected && Volatile.Read(ref _redisHealthy) == 1;

    public async ValueTask<ReadModelCacheEntry?> GetAsync(
        long clanId,
        string kind,
        string id,
        CancellationToken cancellationToken)
    {
        var cacheKey = new MonzeCacheKey(clanId, kind, id);
        if (_l1.TryGet(cacheKey, out ReadModelCacheEntry entry))
        {
            L1Hits.Add(1);
            return entry;
        }

        if (RedisTemporarilyUnavailable())
        {
            return null;
        }

        var key = RedisKey(cacheKey);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var generation = Generation(cacheKey);
            var load = _loads.GetOrAdd(
                cacheKey,
                _ => new Lazy<Task<ReadModelCacheEntry?>>(
                    () => LoadFromL2Async(cacheKey, key, generation),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            ReadModelCacheEntry? result;
            try
            {
                result = await load.Value.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                // Remove only the load that this caller joined. A newer load may have
                // replaced the entry after an invalidation; removing by key alone can
                // break single-flight and cause a second L2 read.
                ((ICollection<KeyValuePair<MonzeCacheKey, Lazy<Task<ReadModelCacheEntry?>>>>)_loads)
                    .Remove(new KeyValuePair<MonzeCacheKey, Lazy<Task<ReadModelCacheEntry?>>>(cacheKey, load));
            }

            if (result.HasValue)
            {
                L2Hits.Add(1);
                return result;
            }

            // An invalidation can arrive after the generation snapshot but before
            // StringGet completes. Retry once so that this safe stale-read rejection
            // does not force an avoidable PostgreSQL fallback on the caller.
            if (attempt == 0
                && Generation(cacheKey) != generation
                && !RedisTemporarilyUnavailable())
            {
                continue;
            }

            L2Misses.Add(1);
            return null;
        }

        return null;
    }

    public async ValueTask<bool> SetAsync(
        long clanId,
        string kind,
        string id,
        ReadModelCacheEntry entry,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        var payloadBytes = Encoding.UTF8.GetByteCount(entry.Payload);
        if (payloadBytes > MaxEntrySize)
        {
            return false;
        }

        var cacheKey = new MonzeCacheKey(clanId, kind, id);
        var effectiveTtl = Jitter(ttl);
        const string script = """
            local current = redis.call('GET', KEYS[1])
            local currentVersion = 0
            if current then
              local newline = string.find(current, '\n', 1, true)
              if newline then currentVersion = tonumber(string.sub(current, 1, newline - 1)) or 0 end
            end
            local nextVersion = tonumber(ARGV[1])
            if nextVersion > currentVersion then
              redis.call('SET', KEYS[1], ARGV[1] .. '\n' .. ARGV[2], 'EX', ARGV[3])
              return 1
            end
            return 0
            """;
        if (RedisTemporarilyUnavailable())
        {
            return false;
        }

        var key = RedisKey(cacheKey);

        RedisResult result;
        try
        {
            result = await _database.ScriptEvaluateAsync(
                script,
                new RedisKey[] { key },
                new RedisValue[] { entry.Version, entry.Payload, Math.Max(1, (int)effectiveTtl.TotalSeconds) }).ConfigureAwait(false);
        }
        catch (RedisException)
        {
            MarkRedisFailure();
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if ((int)result != 1)
        {
            return false;
        }

        MarkRedisHealthy();
        BumpGeneration(cacheKey);
        try
        {
            await PublishInvalidationAsync(key, entry.Version).ConfigureAwait(false);
        }
        catch (RedisException)
        {
            // The value is already committed. Pub/Sub is only a fast invalidation
            // path; TTL and the generation check remain the correctness boundary.
            MarkRedisFailure();
        }
        AddL1(cacheKey, entry, effectiveTtl);
        return true;
    }

    public async ValueTask InvalidateAsync(
        long clanId,
        string kind,
        string id,
        long version,
        CancellationToken cancellationToken)
    {
        var cacheKey = new MonzeCacheKey(clanId, kind, id);
        BumpGeneration(cacheKey);
        if (!_l1.TryGet(cacheKey, out ReadModelCacheEntry current)
            || current.Version <= version)
        {
            _l1.Remove(cacheKey);
        }

        if (RedisTemporarilyUnavailable())
        {
            return;
        }

        var key = RedisKey(cacheKey);

        const string script = """
            local current = redis.call('GET', KEYS[1])
            local currentVersion = 0
            if current then
              local newline = string.find(current, '\n', 1, true)
              if newline then currentVersion = tonumber(string.sub(current, 1, newline - 1)) or 0 end
            end
            local nextVersion = tonumber(ARGV[1])
            if nextVersion >= currentVersion then
              redis.call('SET', KEYS[1], ARGV[1] .. '\n', 'EX', ARGV[2])
              return 1
            end
            return 0
            """;
        RedisResult result;
        try
        {
            result = await _database.ScriptEvaluateAsync(
                script,
                new RedisKey[] { key },
                new RedisValue[] { version, 30 }).ConfigureAwait(false);
        }
        catch (RedisException)
        {
            MarkRedisFailure();
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if ((int)result != 1)
        {
            _logger.LogInformation("Redis cache invalidation skipped because a newer version is already stored.");
            return;
        }

        MarkRedisHealthy();
        _logger.LogInformation("Redis cache invalidation committed.");

        try
        {
            await PublishInvalidationAsync(key, version).ConfigureAwait(false);
        }
        catch (RedisException)
        {
            MarkRedisFailure();
        }
    }

    public void Dispose()
    {
        _multiplexer.ConnectionFailed -= _connectionFailedHandler;
        _multiplexer.ConnectionRestored -= _connectionRestoredHandler;
        try
        {
            _subscriber.Unsubscribe(
                RedisChannel.Literal(_prefix + InvalidationSuffix),
                _invalidationHandler);
        }
        catch (RedisException)
        {
            // Disposal must not turn an already degraded cache into a host shutdown failure.
        }
        _l1.Dispose();
    }

    private string RedisKey(MonzeCacheKey cacheKey)
        => _prefix
            + cacheKey.ClanId.ToString(CultureInfo.InvariantCulture)
            + ":"
            + cacheKey.Kind
            + ":"
            + cacheKey.Id;

    private void AddL1(MonzeCacheKey key, ReadModelCacheEntry entry, TimeSpan ttl)
    {
        if (_l1.TryGet(key, out ReadModelCacheEntry current)
            && current.Version > entry.Version)
        {
            return;
        }

        _l1.Set(
            key,
            entry,
            ttl,
            Math.Min(MaxEntrySize, Math.Max(1, Encoding.UTF8.GetByteCount(entry.Payload))));
    }

    private async Task<ReadModelCacheEntry?> LoadFromL2Async(
        MonzeCacheKey cacheKey,
        string key,
        long generation)
    {
        RedisValue value;
        try
        {
            value = await _database.StringGetAsync(key).ConfigureAwait(false);
        }
        catch (RedisException)
        {
            MarkRedisFailure();
            return null;
        }
        MarkRedisHealthy();
        if (Generation(cacheKey) != generation
            || !value.HasValue
            || !TryDecode(value, out var entry))
        {
            return null;
        }

        if (Generation(cacheKey) != generation)
        {
            return null;
        }

        AddL1(cacheKey, entry, Jitter(TimeSpan.FromMinutes(5)));
        return entry;
    }

    private static TimeSpan Jitter(TimeSpan ttl)
    {
        var milliseconds = ttl.TotalMilliseconds;
        if (milliseconds <= 0)
        {
            return TimeSpan.FromSeconds(1);
        }

        return TimeSpan.FromMilliseconds(
            milliseconds + Random.Shared.NextDouble() * milliseconds * 0.1);
    }

    private long Generation(MonzeCacheKey key)
        => _generations.TryGetValue(key, out var generation) ? generation : 0;

    private bool RedisTemporarilyUnavailable()
        => Environment.TickCount64 < Volatile.Read(ref _redisRetryAfterMilliseconds);

    private void MarkRedisFailure()
    {
        Volatile.Write(ref _redisHealthy, 0);
        var now = Environment.TickCount64;
        var retryAfter = now + RedisFailureCooldownMilliseconds;
        var previous = Volatile.Read(ref _redisRetryAfterMilliseconds);
        if (previous > now
            || Interlocked.CompareExchange(ref _redisRetryAfterMilliseconds, retryAfter, previous) != previous)
        {
            return;
        }

        RedisErrors.Add(1);
        _logger.LogWarning(
            "Redis cache degraded; using PostgreSQL fallback for the next {CooldownMilliseconds} ms. Cause={Cause}.",
            RedisFailureCooldownMilliseconds,
            "redis-operation-failed");
    }

    private void MarkRedisHealthy()
    {
        Volatile.Write(ref _redisHealthy, 1);
        Volatile.Write(ref _redisRetryAfterMilliseconds, 0);
    }

    private async Task SubscribeToInvalidationsAsync()
    {
        try
        {
            await _subscriber.SubscribeAsync(
                RedisChannel.Literal(_prefix + InvalidationSuffix),
                _invalidationHandler).ConfigureAwait(false);
            MarkRedisHealthy();
        }
        catch (RedisException ex)
        {
            MarkRedisFailure();
            _logger.LogWarning(
                ex,
                "Redis invalidation subscription unavailable; continuing with PostgreSQL fallback.");
        }
    }

    private void BumpGeneration(MonzeCacheKey key)
        => _generations.AddOrUpdate(key, 1, static (_, value) => unchecked(value + 1));

    private static bool TryDecode(RedisValue value, out ReadModelCacheEntry entry)
    {
        var text = value.ToString();
        var newline = text.IndexOf('\n');
        if (newline <= 0
            || !long.TryParse(
                text.AsSpan(0, newline),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var version))
        {
            entry = default;
            return false;
        }

        entry = new ReadModelCacheEntry(version, text[(newline + 1)..]);
        return true;
    }

    private void InvalidateLocal(RedisValue value)
    {
        if (!value.HasValue)
        {
            return;
        }

        var message = value.ToString();
        var publisherSeparator = message.IndexOf('|');
        if (publisherSeparator <= 0
            || string.Equals(message[..publisherSeparator], _publisherId, StringComparison.Ordinal))
        {
            return;
        }

        var versionSeparator = message.IndexOf('|', publisherSeparator + 1);
        var version = long.MaxValue;
        string key;
        if (versionSeparator > publisherSeparator
            && long.TryParse(
                message.AsSpan(publisherSeparator + 1, versionSeparator - publisherSeparator - 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var publishedVersion))
        {
            version = publishedVersion;
            key = message[(versionSeparator + 1)..];
        }
        else
        {
            // Read old-format invalidations during a rolling upgrade.
            key = message[(publisherSeparator + 1)..];
        }

        if (!TryParseRedisKey(key, out var cacheKey))
        {
            _logger.LogWarning("Ignoring invalid Monze cache invalidation key.");
            return;
        }

        BumpGeneration(cacheKey);
        if (_l1.TryGet(cacheKey, out ReadModelCacheEntry current)
            && current.Version <= version)
        {
            _l1.Remove(cacheKey);
        }
    }

    private bool TryParseRedisKey(string key, out MonzeCacheKey cacheKey)
    {
        cacheKey = default;
        if (!key.StartsWith(_prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = key.AsSpan(_prefix.Length);
        var clanSeparator = body.IndexOf(':');
        if (clanSeparator <= 0
            || !long.TryParse(
                body[..clanSeparator],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var clanId))
        {
            return false;
        }

        body = body[(clanSeparator + 1)..];
        var kindSeparator = body.IndexOf(':');
        if (kindSeparator <= 0 || kindSeparator == body.Length - 1)
        {
            return false;
        }

        var kind = body[..kindSeparator].ToString();
        var id = body[(kindSeparator + 1)..].ToString();
        if (id.Length == 0)
        {
            return false;
        }

        cacheKey = new MonzeCacheKey(clanId, kind, id);
        return true;
    }

    private async Task PublishInvalidationAsync(string key, long version)
        => await _subscriber.PublishAsync(
            RedisChannel.Literal(_prefix + InvalidationSuffix),
            _publisherId
                + "|"
                + version.ToString(CultureInfo.InvariantCulture)
                + "|"
                + key).ConfigureAwait(false);
}
