using Xunit;

namespace Monze.Tests;

public sealed class MessageGapBatchTests
{
    [Fact]
    public void Keeps_the_highest_message_id_per_valid_clan_channel_pair()
    {
        var batch = new MessageGapBatch(2);

        batch.Add(7, 11, 100);
        batch.Add(7, 11, 90);
        batch.Add(7, 11, 120);
        batch.Add(7, 12, 5);
        batch.Add(0, 12, 500);
        batch.Add(7, 0, 500);
        batch.Add(7, 12, 0);

        Assert.Equal(2, batch.Count);
        Assert.Equal(120, batch.Entries[new ChannelPolicyKey(7, 11)]);
        Assert.Equal(5, batch.Entries[new ChannelPolicyKey(7, 12)]);

        batch.Clear();

        Assert.Empty(batch.Entries);
    }
}
