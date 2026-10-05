using Monze.Domain;
using Xunit;

namespace Monze.Tests;

public sealed class AiReplyWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reply_window_accepts_the_exact_one_hour_boundary()
    {
        Assert.True(AiReplyWindow.Contains(Now.AddHours(-1), Now, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Reply_window_rejects_old_unknown_and_future_timestamps()
    {
        Assert.False(AiReplyWindow.Contains(Now.AddHours(-1).AddTicks(-1), Now, TimeSpan.FromHours(1)));
        Assert.False(AiReplyWindow.Contains(null, Now, TimeSpan.FromHours(1)));
        Assert.False(AiReplyWindow.Contains(Now.AddTicks(1), Now, TimeSpan.FromHours(1)));
    }
}
