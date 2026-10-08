using System.Collections.Concurrent;
using Monze.Application;

namespace Monze.Infrastructure.Caching;

internal sealed class MonzeL1Cache : IDisposable
{
    private const long MaxCacheBytes = 64L * 1024 * 1024;
    private const int MaxEntryBytes = 64 * 1024;
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<MonzeCacheKey, Entry> _entries = new();
    private readonly ConcurrentQueue<EvictionToken> _evictionQueue = new();
    private readonly TimeProvider _time;
    private readonly ITimer _maintenanceTimer;
    private long _usedBytes;
    private long _nextToken;

    public MonzeL1Cache(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _maintenanceTimer = _time.CreateTimer(
            static state => ((MonzeL1Cache)state!).RemoveExpiredEntries(),
            this,
            MaintenanceInterval,
            MaintenanceInterval);
    }

    public bool TryGet(MonzeCacheKey key, out ReadModelCacheEntry entry)
    {
        if (!_entries.TryGetValue(key, out var cached))
        {
            entry = default;
            return false;
        }

        if (cached.ExpiresAt <= _time.GetTimestamp())
        {
            Remove(key, cached);
            entry = default;
            return false;
        }

        entry = cached.Value;
        return true;
    }

    public void Set(
        MonzeCacheKey key,
        ReadModelCacheEntry value,
        TimeSpan ttl,
        int sizeBytes)
    {
        if (sizeBytes <= 0 || sizeBytes > MaxEntryBytes)
        {
            return;
        }

        var expiresAt = _time.GetTimestamp() + ToTimestampTicks(ttl);
        var replacement = new Entry(
            value,
            expiresAt,
            sizeBytes,
            Interlocked.Increment(ref _nextToken));

        while (true)
        {
            if (!_entries.TryGetValue(key, out var current))
            {
                if (_entries.TryAdd(key, replacement))
                {
                    Interlocked.Add(ref _usedBytes, sizeBytes);
                    _evictionQueue.Enqueue(new EvictionToken(key, replacement.Token));
                    TrimToLimit();
                    return;
                }

                continue;
            }

            if (current.Value.Version > value.Version)
            {
                return;
            }

            if (_entries.TryUpdate(key, replacement, current))
            {
                Interlocked.Add(ref _usedBytes, sizeBytes - current.SizeBytes);
                _evictionQueue.Enqueue(new EvictionToken(key, replacement.Token));
                TrimToLimit();
                return;
            }
        }
    }

    public void Remove(MonzeCacheKey key)
    {
        if (_entries.TryRemove(key, out var removed))
        {
            Interlocked.Add(ref _usedBytes, -removed.SizeBytes);
        }
    }

    public void Dispose()
    {
        _maintenanceTimer.Dispose();
        _entries.Clear();
        Interlocked.Exchange(ref _usedBytes, 0);
    }

    private long ToTimestampTicks(TimeSpan duration)
    {
        var ticks = duration.TotalSeconds * _time.TimestampFrequency;
        return Math.Max(1, (long)Math.Min(ticks, long.MaxValue));
    }

    private void RemoveExpiredEntries()
    {
        var now = _time.GetTimestamp();
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                Remove(pair.Key, pair.Value);
            }
        }

        TrimToLimit();
    }

    private void TrimToLimit()
    {
        while (Volatile.Read(ref _usedBytes) > MaxCacheBytes
            && _evictionQueue.TryDequeue(out var token))
        {
            if (_entries.TryGetValue(token.Key, out var current)
                && current.Token == token.Token)
            {
                Remove(token.Key, current);
            }
        }
    }

    private void Remove(MonzeCacheKey key, Entry expected)
    {
        if (((ICollection<KeyValuePair<MonzeCacheKey, Entry>>)_entries)
            .Remove(new KeyValuePair<MonzeCacheKey, Entry>(key, expected)))
        {
            Interlocked.Add(ref _usedBytes, -expected.SizeBytes);
        }
    }

    private readonly struct Entry
    {
        public Entry(
            ReadModelCacheEntry value,
            long expiresAt,
            int sizeBytes,
            long token)
        {
            Value = value;
            ExpiresAt = expiresAt;
            SizeBytes = sizeBytes;
            Token = token;
        }

        public ReadModelCacheEntry Value { get; }

        public long ExpiresAt { get; }

        public int SizeBytes { get; }

        public long Token { get; }
    }

    private readonly struct EvictionToken
    {
        public EvictionToken(MonzeCacheKey key, long token)
        {
            Key = key;
            Token = token;
        }

        public MonzeCacheKey Key { get; }

        public long Token { get; }
    }
}
