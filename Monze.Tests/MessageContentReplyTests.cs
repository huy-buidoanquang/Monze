using Mezon.Net.Client;
using Monze.Ui;
using Xunit;

namespace Monze.Tests;

public sealed class MessageContentReplyTests
{
    [Fact]
    public void Apply_adds_the_durable_reply_target_without_losing_embed_content()
    {
        var original = MessageContent.Parse(
            "{\"embed\":[{\"title\":\"summary\",\"color\":16711680}],\"future\":{\"value\":1}}");

        var updated = MessageContentReply.Apply(original, 1840651258350000001L);

        Assert.Equal(1840651258350000001L, updated.ReplyToMessageId);
        Assert.Equal("summary", Assert.Single(updated.Embeds!).Title);
        Assert.Equal("16711680", Assert.Single(updated.Embeds!).Color);
        Assert.True(updated.UnknownExtensions!.ContainsKey("future"));
    }

    [Fact]
    public void Apply_returns_the_original_payload_when_the_reply_is_already_correct()
    {
        var original = MessageContent.Parse(
            "{\"rpl\":1840651258350000001,\"embed\":[{\"title\":\"summary\"}]}");

        var updated = MessageContentReply.Apply(original, 1840651258350000001L);

        Assert.Same(original, updated);
    }

    [Fact]
    public void Apply_replaces_a_stale_reply_with_the_durable_invitation_id()
    {
        var original = MessageContent.Parse(
            "{\"rpl\":42,\"embed\":[{\"title\":\"summary\",\"color\":65280}]}");

        var updated = MessageContentReply.Apply(original, 1840651258350000001L);

        Assert.Equal(1840651258350000001L, updated.ReplyToMessageId);
        Assert.Equal("summary", Assert.Single(updated.Embeds!).Title);
        Assert.Equal("65280", Assert.Single(updated.Embeds!).Color);
    }
}
