using System.Collections.Concurrent;

namespace Monze;

internal sealed class VoiceSnapshotGuard
{
    private readonly ConcurrentDictionary<long, long> _clanGenerations = new();
    private readonly ConcurrentDictionary<long, (long Epoch, long Generation)> _ready = new();
    private long _epoch;

    public (long Epoch, long Generation) BeginRefresh(long clanId)
    {
        _ready.TryRemove(clanId, out _);
        return (Volatile.Read(ref _epoch), _clanGenerations.GetOrAdd(clanId, 0));
    }

    public void Invalidate(long clanId)
    {
        _clanGenerations.AddOrUpdate(clanId, 1, static (_, current) => current + 1);
        _ready.TryRemove(clanId, out _);
    }

    public void InvalidateAll()
    {
        Interlocked.Increment(ref _epoch);
        _ready.Clear();
    }

    public bool IsCurrent(long clanId, (long Epoch, long Generation) version)
        => version.Epoch == Volatile.Read(ref _epoch)
            && version.Generation == _clanGenerations.GetOrAdd(clanId, 0);

    public bool TryMarkReady(long clanId, (long Epoch, long Generation) version)
    {
        if (!IsCurrent(clanId, version))
        {
            return false;
        }

        _ready[clanId] = version;
        if (IsCurrent(clanId, version))
        {
            return true;
        }

        ((ICollection<KeyValuePair<long, (long Epoch, long Generation)>>)_ready)
            .Remove(new KeyValuePair<long, (long Epoch, long Generation)>(clanId, version));
        return false;
    }

    public bool IsReady(long clanId)
        => _ready.TryGetValue(clanId, out var version)
            && IsCurrent(clanId, version);
}
