using Monze.Application;
using Monze.Infrastructure.Caching;
using Xunit;

namespace Monze.Tests;

public sealed class WelcomeSetupDraftStoreTests
{
    [Fact]
    public void Set_rejects_new_entries_beyond_the_memory_bound_but_updates_existing_key()
    {
        var store = new MemoryWelcomeSetupDraftStore();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 4_096; i++)
        {
            store.Set(
                new WelcomeSetupDraftKey(i + 1, 20, 30),
                new WelcomeSettings(false, null, 0),
                now);
        }

        var rejected = new WelcomeSetupDraftKey(5_000, 20, 30);
        store.Set(rejected, new WelcomeSettings(false, null, 0), now);
        Assert.False(store.TryGet(rejected, now, out _));

        var existing = new WelcomeSetupDraftKey(1, 20, 30);
        store.Set(existing, new WelcomeSettings(true, "updated", 1), now);
        Assert.True(store.TryGet(existing, now, out var settings));
        Assert.True(settings.Enabled);
        Assert.Equal("updated", settings.Text);
    }
}
