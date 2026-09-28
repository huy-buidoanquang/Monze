using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Monze.Application.Ingress;
using System.Threading.Channels;

[MemoryDiagnoser]
[ShortRunJob]
public class MonzeCapacityBenchmarks
{
    [Params(1, 10, 100, 1000)]
    public int ClanCount { get; set; }

    [Params(100)]
    public int ActiveClanCount { get; set; }

    private PartitionedIngressQueue<IngressEnvelope> _queue = null!;
    private Dictionary<long, byte> _clanRegistry = null!;
    private IngressEnvelope[] _events = null!;
    private ChannelReader<IngressEnvelope>[] _readers = null!;
    private int _nextEvent;

    [GlobalSetup]
    public void Setup()
    {
        var active = Math.Min(ActiveClanCount, ClanCount);
        _queue = new PartitionedIngressQueue<IngressEnvelope>(16, 8192);
        _clanRegistry = new Dictionary<long, byte>(ClanCount);
        for (var i = 1; i <= ClanCount; i++)
        {
            _clanRegistry.Add(i, 0);
        }

        _readers = new ChannelReader<IngressEnvelope>[_queue.PartitionCount];
        for (var i = 0; i < _readers.Length; i++)
        {
            _readers[i] = _queue.GetReader(i);
        }

        _events = new IngressEnvelope[Math.Max(1, active)];
        for (var i = 0; i < _events.Length; i++)
        {
            var clanId = i + 1L;
            _events[i] = new IngressEnvelope(clanId, clanId * 10, clanId * 100, clanId * 1000);
            _queue.TryWrite(clanId, _events[i]);
            _readers[PartitionFor(clanId)].TryRead(out _);
        }

        _nextEvent = 0;
    }

    [Benchmark]
    public bool ActiveClanIngressTryWrite()
    {
        var item = _events[_nextEvent++];
        if (_nextEvent == _events.Length)
        {
            _nextEvent = 0;
        }

        var written = _queue.TryWrite(item.ClanId, item);
        _readers[PartitionFor(item.ClanId)].TryRead(out _);
        return written && _clanRegistry.ContainsKey(item.ClanId);
    }

    private static int PartitionFor(long clanId)
    {
        var hash = (ulong)clanId;
        hash ^= hash >> 33;
        hash *= 0xff51afd7ed558ccdUL;
        hash ^= hash >> 33;
        hash *= 0xc4ceb9fe1a85ec53UL;
        hash ^= hash >> 33;
        return (int)(hash & 15);
    }
}
