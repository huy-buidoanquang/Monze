using Monze.Application.Ingress;
using Xunit;

namespace Monze.Tests;

public sealed class PartitionedIngressQueueTests
{
    [Fact]
    public void Keeps_a_key_in_one_bounded_lane()
    {
        var queue = new PartitionedIngressQueue<IngressItem>(4, 8);

        Assert.True(queue.TryWrite(2104, new IngressItem(1)));
        Assert.True(queue.TryWrite(2104, new IngressItem(2)));
        Assert.False(queue.TryWrite(2104, new IngressItem(3)));

        var values = new List<int>();
        for (var i = 0; i < queue.PartitionCount; i++)
        {
            while (queue.GetReader(i).TryRead(out var item))
            {
                values.Add(item.Value);
            }
        }

        Assert.Equal([1, 2], values);
        Assert.Equal(8, queue.Capacity);
    }

    [Fact]
    public void Completion_is_applied_to_every_lane()
    {
        var queue = new PartitionedIngressQueue<IngressItem>(2, 4);

        queue.Complete();

        Assert.False(queue.TryWrite(1, new IngressItem(1)));
        Assert.False(queue.TryWrite(2, new IngressItem(2)));
    }

}
