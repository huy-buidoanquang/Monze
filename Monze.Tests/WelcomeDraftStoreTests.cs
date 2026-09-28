using Monze.Application;
using Monze.Infrastructure.Caching;
using Xunit;

namespace Monze.Tests;

public sealed class WelcomeDraftStoreTests
{
    [Fact]
    public void Ticket_is_bound_to_clan_and_channel_and_can_only_be_claimed_once()
    {
        var store = new MemoryWelcomeDraftStore();
        var now = DateTimeOffset.UtcNow;
        var created = store.Create(10, 20, true, new WelcomeEmbedSettings(Title: "Xin chào"), now);

        Assert.False(store.TryClaim(created.Token, 11, 20, now, out _));
        Assert.False(store.TryClaim(created.Token, 10, 21, now, out _));
        Assert.True(store.TryClaim(created.Token, 10, 20, now, out var claimed));
        Assert.Equal("Xin chào", claimed.Draft.Title);
        Assert.False(store.TryClaim(created.Token, 10, 20, now, out _));

        store.Complete(claimed);
    }

    [Fact]
    public void Expired_ticket_is_rejected()
    {
        var store = new MemoryWelcomeDraftStore();
        var createdAt = DateTimeOffset.UtcNow;
        var created = store.Create(10, 20, false, new WelcomeEmbedSettings(), createdAt);

        Assert.False(store.TryClaim(
            created.Token,
            10,
            20,
            created.ExpiresAt.AddTicks(1),
            out _));
    }

    [Fact]
    public void Release_allows_retry_after_a_transient_save_failure()
    {
        var store = new MemoryWelcomeDraftStore();
        var now = DateTimeOffset.UtcNow;
        var created = store.Create(10, 20, true, new WelcomeEmbedSettings(), now);

        Assert.True(store.TryClaim(created.Token, 10, 20, now, out var claimed));
        store.Release(claimed);
        Assert.True(store.TryClaim(created.Token, 10, 20, now, out var retried));

        store.Complete(retried);
    }
}
