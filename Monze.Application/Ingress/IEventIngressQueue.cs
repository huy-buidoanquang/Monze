using System.Threading.Channels;

namespace Monze.Application.Ingress;

public interface IEventIngressQueue<T>
    where T : struct
{
    int PartitionCount { get; }

    int Capacity { get; }

    ChannelReader<T> GetReader(int partition);

    bool TryWrite(long key, T item);

    void Complete(Exception? error = null);
}
