using System.Collections.Concurrent;
using Monze.Application;

namespace Monze.Infrastructure.Caching;

public sealed class MemoryWelcomeSetupDraftStore : IWelcomeSetupDraftStore
{
    private const int MaxEntries = 4_096;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly object _capacityGate = new();
    private readonly ConcurrentDictionary<WelcomeSetupDraftKey, WelcomeSetupDraftState> _drafts = new();

    public bool TryGet(WelcomeSetupDraftKey key, DateTimeOffset now, out WelcomeSettings settings)
    {
        CleanupExpired(now);
        if (_drafts.TryGetValue(key, out var state) && state.ExpiresAt > now)
        {
            settings = state.Settings;
            return true;
        }

        settings = default!;
        return false;
    }

    public void Set(WelcomeSetupDraftKey key, WelcomeSettings settings, DateTimeOffset now)
    {
        lock (_capacityGate)
        {
            CleanupExpired(now);
            if (!_drafts.ContainsKey(key) && _drafts.Count >= MaxEntries)
            {
                return;
            }

            _drafts[key] = new WelcomeSetupDraftState(settings, now.Add(Lifetime));
        }
    }

    public void Remove(WelcomeSetupDraftKey key)
        => _drafts.TryRemove(key, out _);

    private void CleanupExpired(DateTimeOffset now)
    {
        foreach (var pair in _drafts)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _drafts.TryRemove(pair.Key, out _);
            }
        }
    }
}
