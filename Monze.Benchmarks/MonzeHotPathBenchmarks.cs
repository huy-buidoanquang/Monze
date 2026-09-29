using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Monze.Application.Ingress;
using Monze.Application.Commands;
using Monze.Domain;
using System.Threading.Channels;

[MemoryDiagnoser]
[ShortRunJob]
public class MonzeHotPathBenchmarks
{
    private PartitionedIngressQueue<IngressEnvelope> _queue = null!;
    private ChannelReader<IngressEnvelope> _reader = null!;
    private IngressEnvelope _envelope;
    private CommandArguments _commandArguments;
    private MonzeCommandRateLimiter _rateLimiter = null!;
    private DateTimeOffset _rateLimitNow;

    [GlobalSetup]
    public void Setup()
    {
        _queue = new PartitionedIngressQueue<IngressEnvelope>(16, 8192);
        _envelope = new IngressEnvelope(206, 1001, 9001, 1);
        _commandArguments = new CommandArguments(new[] { "role" });
        _rateLimiter = new MonzeCommandRateLimiter(new MonzeRateLimitOptions(
            UserLimit: 1_000_000,
            UserWindow: TimeSpan.FromMinutes(1),
            AiLimit: 1_000_000,
            AiWindow: TimeSpan.FromMinutes(1),
            MeetingLimit: 1_000_000,
            MeetingWindow: TimeSpan.FromMinutes(1),
            AdminLimit: 1_000_000,
            AdminWindow: TimeSpan.FromMinutes(1),
            MaxEntries: 16));
        _rateLimitNow = DateTimeOffset.UtcNow;
        _rateLimiter.TryAcquire(206, 1001, MonzeCommandNames.Role, _rateLimitNow, out _);
        _reader = _queue.GetReader(0);
        _queue.TryWrite(_envelope.ClanId, _envelope);
        _reader.TryRead(out _);
    }

    [Benchmark]
    public bool PartitionedIngressTryWrite()
    {
        var written = _queue.TryWrite(_envelope.ClanId, _envelope);
        _reader.TryRead(out _);
        return written;
    }

    [Benchmark]
    public string CommandArgumentsSingleItem()
        => _commandArguments.Slice(0).Join(' ');

    [Benchmark]
    public bool CommandRateLimitKnownKey()
        => _rateLimiter.TryAcquire(
            206,
            1001,
            MonzeCommandNames.Role,
            _rateLimitNow,
            out _);
}
