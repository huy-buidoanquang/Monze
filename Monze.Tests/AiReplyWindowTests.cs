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

    [Fact]
    public void Reply_summary_keeps_ordinary_member_messages()
    {
        Assert.True(AiReplyWindow.IsConversationMessage(11, 10, 501, 900, "Anh Nam chuẩn bị slide", "*"));
    }

    [Fact]
    public void Reply_summary_skips_commands_and_bot_output_after_the_anchor()
    {
        Assert.False(AiReplyWindow.IsConversationMessage(11, 10, 501, 900, "*ai simplify nội dung", "*"));
        Assert.False(AiReplyWindow.IsConversationMessage(12, 10, 501, 900, "  *monze help", "*"));
        Assert.False(AiReplyWindow.IsConversationMessage(13, 10, 900, 900, "Kết quả tóm tắt trước", "*"));
    }

    [Fact]
    public void Reply_summary_always_keeps_the_replied_message()
    {
        Assert.True(AiReplyWindow.IsConversationMessage(10, 10, 900, 900, "*ai composer bản nháp", "*"));
    }
}
