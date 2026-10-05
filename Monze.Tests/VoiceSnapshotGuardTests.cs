using Xunit;

namespace Monze.Tests;

public sealed class VoiceSnapshotGuardTests
{
    [Fact]
    public void Rejects_a_snapshot_invalidated_by_a_realtime_event()
    {
        var guard = new VoiceSnapshotGuard();
        var generation = guard.BeginRefresh(7);

        guard.Invalidate(7);

        Assert.False(guard.TryMarkReady(7, generation));
        Assert.False(guard.IsReady(7));
    }

    [Fact]
    public void A_stale_completion_cannot_remove_a_newer_ready_snapshot()
    {
        var guard = new VoiceSnapshotGuard();
        var stale = guard.BeginRefresh(7);
        guard.Invalidate(7);
        var current = guard.BeginRefresh(7);

        Assert.True(guard.TryMarkReady(7, current));
        Assert.False(guard.TryMarkReady(7, stale));
        Assert.True(guard.IsReady(7));
    }

    [Fact]
    public void Invalidate_all_invalidates_every_ready_snapshot()
    {
        var guard = new VoiceSnapshotGuard();
        var first = guard.BeginRefresh(7);
        var second = guard.BeginRefresh(8);
        Assert.True(guard.TryMarkReady(7, first));
        Assert.True(guard.TryMarkReady(8, second));

        guard.InvalidateAll();

        Assert.False(guard.IsReady(7));
        Assert.False(guard.IsReady(8));
    }
}
