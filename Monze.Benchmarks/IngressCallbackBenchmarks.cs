using System.Reflection;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Mezon.Net.Internal.Api;
using Mezon.Net.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Monze;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Application.Ingress;
using Monze.Hosting;

/// <summary>
/// The SDK message callback as MonzeBot registers it (EnqueueMessageAsync),
/// from the event to the accepted TryWrite. The consumer side is drained from
/// the key's own lane so every write takes the accepted path.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class IngressCallbackBenchmarks
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private MonzeBot _bot = null!;
    private Func<ChannelMessageEventData, Task> _callback = null!;
    private ChannelReader<ChannelMessageEventData> _reader = null!;
    private ChannelMessageEventData _message;

    [GlobalSetup]
    public void Setup()
    {
        var configuration = new ConfigurationBuilder().Build();
        _bot = new MonzeBot(
            configuration,
            app: null!,
            clans: null!,
            authorization: null!,
            welcome: null!,
            meeting: null!,
            scheduledMeeting: null!,
            scheduling: null!,
            outbox: null!,
            messageHistory: null!,
            userProfiles: null!,
            commandInbox: null!,
            interactionInbox: null!,
            welcomeSetupDrafts: null!,
            transcript: null!,
            summaryComposer: null!,
            readModelCache: new DisabledReadModelCache(),
            policyCache: null!,
            commandOptions: MonzeCommandOptions.Default,
            connectionRetryOptions: MonzeConnectionRetryOptions.From(configuration),
            commandRateLimiter: null!,
            readiness: new StartupReadiness(),
            time: TimeProvider.System,
            timings: MonzeWorkerTimings.From(configuration),
            logger: NullLogger<MonzeBot>.Instance);
        _callback = typeof(MonzeBot).GetMethod("EnqueueMessageAsync", Private)!
            .CreateDelegate<Func<ChannelMessageEventData, Task>>(_bot);
        var queue = (PartitionedIngressQueue<ChannelMessageEventData>)typeof(MonzeBot)
            .GetField("_messageIngress", Private)!
            .GetValue(_bot)!;
        _message = CreateMessage(clanId: 206, channelId: 1001, messageId: 9001);
        _reader = queue.GetReader(queue.GetPartition(206));
    }

    [GlobalCleanup]
    public void VerifyNothingWasDropped()
    {
        var dropped = (long)typeof(MonzeBot).GetField("_droppedMessages", Private)!.GetValue(_bot)!;
        if (dropped > 0)
        {
            throw new InvalidOperationException(
                $"{dropped} messages were dropped; the benchmark did not measure the accepted path.");
        }
    }

    [Benchmark]
    [ZeroAllocationGate]
    public Task MessageCallbackAccepted()
    {
        var completion = _callback(_message);
        _reader.TryRead(out _);
        return completion;
    }

    private static ChannelMessageEventData CreateMessage(long clanId, long channelId, long messageId)
    {
        var proto = new ChannelMessage { ClanId = clanId, ChannelId = channelId, MessageId = messageId };
        var constructor = typeof(ChannelMessageResponse).GetConstructor(Private, [typeof(ChannelMessage)])
            ?? throw new InvalidOperationException("ChannelMessageResponse(ChannelMessage) was not found in the SDK.");
        return (ChannelMessageResponse)constructor.Invoke([proto]);
    }
}
