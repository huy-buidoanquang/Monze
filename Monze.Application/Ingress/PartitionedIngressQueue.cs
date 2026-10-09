using System.Threading.Channels;

namespace Monze.Application.Ingress;

/// <summary>
/// A fixed-capacity set of single-reader lanes selected by a stable numeric key.
/// The queue is intended for realtime ingress where ordering is required within a
/// key, while unrelated keys may be consumed concurrently.
/// </summary>
public sealed class PartitionedIngressQueue<T> : IEventIngressQueue<T>
    where T : struct
{
    private readonly Channel<T>[] _lanes;
    private readonly int _partitionMask;

    public PartitionedIngressQueue(int partitionCount, int totalCapacity)
    {
        if (partitionCount < 1 || (partitionCount & (partitionCount - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(partitionCount),
                partitionCount,
                "Partition count must be a positive power of two.");
        }

        if (totalCapacity < partitionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalCapacity),
                totalCapacity,
                "Total capacity must be at least the partition count.");
        }

        _lanes = new Channel<T>[partitionCount];
        _partitionMask = partitionCount - 1;
        var capacityPerLane = totalCapacity / partitionCount;
        var lanesWithExtraCapacity = totalCapacity % partitionCount;
        for (var i = 0; i < _lanes.Length; i++)
        {
            var laneCapacity = capacityPerLane + (i < lanesWithExtraCapacity ? 1 : 0);
            _lanes[i] = Channel.CreateBounded<T>(
                new BoundedChannelOptions(laneCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
        }

        PartitionCount = partitionCount;
        Capacity = totalCapacity;
    }

    public int PartitionCount { get; }

    public int Capacity { get; }

    public ChannelReader<T> GetReader(int partition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(partition);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(partition, _lanes.Length);
        return _lanes[partition].Reader;
    }

    public bool TryWrite(long key, T item)
        => _lanes[GetPartition(key)].Writer.TryWrite(item);

    /// <summary>Waits for room in <paramref name="key"/>'s lane (for events that must not be dropped).</summary>
    public ValueTask WriteAsync(long key, T item, CancellationToken cancellationToken = default)
        => _lanes[GetPartition(key)].Writer.WriteAsync(item, cancellationToken);

    public void Complete(Exception? error = null)
    {
        for (var i = 0; i < _lanes.Length; i++)
        {
            _lanes[i].Writer.TryComplete(error);
        }
    }

    /// <summary>The lane that <see cref="TryWrite"/> uses for <paramref name="key"/>.</summary>
    public int GetPartition(long key)
    {
        var hash = (ulong)key;
        hash ^= hash >> 33;
        hash *= 0xff51afd7ed558ccdUL;
        hash ^= hash >> 33;
        hash *= 0xc4ceb9fe1a85ec53UL;
        hash ^= hash >> 33;
        return (int)(hash & (uint)_partitionMask);
    }
}
