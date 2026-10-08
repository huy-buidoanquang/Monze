using System.Diagnostics.Metrics;
using Mezon.Net.Core;

namespace Monze;

internal static class MonzeMetrics
{
    private static readonly Meter Meter = new("Monze", "1.0");

    private static Func<long>? _messageIngressDepth;
    private static Func<long>? _messageGapIngressDepth;
    private static Func<long>? _agentIngressDepth;
    private static Func<long>? _welcomeIngressDepth;
    private static Func<long>? _outboxInFlight;
    private static Func<long>? _agentPendingWriters;
    private static Func<long>? _welcomePendingWriters;

    static MonzeMetrics()
    {
        Meter.CreateObservableGauge(
            "monze.ingress.message.depth",
            static () => Read(_messageIngressDepth),
            "items",
            "Current message ingress queue depth.");
        Meter.CreateObservableGauge(
            "monze.ingress.message_gap.depth",
            static () => Read(_messageGapIngressDepth),
            "items",
            "Current message-gap ingress queue depth.");
        Meter.CreateObservableGauge(
            "monze.ingress.agent.depth",
            static () => Read(_agentIngressDepth),
            "items",
            "Current Agent event ingress queue depth.");
        Meter.CreateObservableGauge(
            "monze.ingress.welcome.depth",
            static () => Read(_welcomeIngressDepth),
            "items",
            "Current welcome ingress queue depth.");
        Meter.CreateObservableGauge(
            "monze.outbox.inflight",
            static () => Read(_outboxInFlight),
            "items",
            "Current outbox delivery attempts in progress.");
        Meter.CreateObservableGauge(
            "monze.ingress.agent.pending_writers",
            static () => Read(_agentPendingWriters),
            "writers",
            "Agent event writers waiting for ingress queue capacity.");
        Meter.CreateObservableGauge(
            "monze.ingress.welcome.pending_writers",
            static () => Read(_welcomePendingWriters),
            "writers",
            "Welcome join writers waiting for ingress queue capacity.");
    }

    internal static readonly Counter<long> MessageIngressDropped =
        Meter.CreateCounter<long>("monze.ingress.message.dropped");

    internal static readonly Counter<long> MessageGapIngressDropped =
        Meter.CreateCounter<long>("monze.ingress.message_gap.dropped");

    internal static readonly Counter<long> AgentIngressBackpressure =
        Meter.CreateCounter<long>("monze.ingress.agent.backpressure");

    internal static readonly Counter<long> AgentEventsIgnored =
        Meter.CreateCounter<long>("monze.agent.events.ignored");

    internal static readonly Counter<long> WelcomeIngressBackpressure =
        Meter.CreateCounter<long>("monze.ingress.welcome.backpressure");

    internal static readonly Counter<long> SocketReconnects =
        Meter.CreateCounter<long>("monze.socket.reconnect");

    internal static readonly Counter<long> SocketDisconnects =
        Meter.CreateCounter<long>("monze.socket.disconnect");

    internal static readonly Counter<long> OutboxClaimed =
        Meter.CreateCounter<long>("monze.outbox.claimed", "items");

    internal static readonly Counter<long> OutboxDelivered =
        Meter.CreateCounter<long>("monze.outbox.delivered", "items");

    internal static readonly Counter<long> OutboxUncertain =
        Meter.CreateCounter<long>("monze.outbox.uncertain", "items");

    internal static readonly Counter<long> OutboxFailed =
        Meter.CreateCounter<long>("monze.outbox.failed", "items");

    internal static readonly Histogram<double> OutboxDueLagMilliseconds =
        Meter.CreateHistogram<double>("monze.outbox.due_lag", "ms");

    internal static readonly Histogram<double> OutboxAttemptDurationMilliseconds =
        Meter.CreateHistogram<double>("monze.outbox.attempt_duration", "ms");

    internal static readonly Counter<long> UpstreamRateLimitDelayed =
        Meter.CreateCounter<long>(
            "monze.upstream.ratelimit.delayed",
            "requests",
            "Mezon requests delayed by the SDK transport rate limiter.");

    internal static readonly Histogram<double> UpstreamRateLimitDelayMilliseconds =
        Meter.CreateHistogram<double>(
            "monze.upstream.ratelimit.delay",
            "ms",
            "Delay the SDK transport rate limiter applied to a Mezon request.");

    internal static readonly UpDownCounter<long> CommandInflight =
        Meter.CreateUpDownCounter<long>("monze.command.inflight", "commands");

    internal static readonly Histogram<double> CommandDurationMilliseconds =
        Meter.CreateHistogram<double>("monze.command.duration", "ms");

    /// <summary>
    /// MezonClientOptions.DefaultRatelimitCallback: the SDK calls it when its
    /// transport limiter delays a request, with the bucket and the delay.
    /// </summary>
    internal static Task RecordUpstreamRateLimit(IRateLimitInfo info)
    {
        var bucket = new KeyValuePair<string, object?>("bucket", info.Bucket);
        UpstreamRateLimitDelayed.Add(1, bucket);
        UpstreamRateLimitDelayMilliseconds.Record(info.ResetAfter.TotalMilliseconds, bucket);
        return Task.CompletedTask;
    }

    internal static void RecordCommand(string module, string outcome, TimeSpan elapsed)
        => CommandDurationMilliseconds.Record(
            elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("module", module),
            new KeyValuePair<string, object?>("outcome", outcome));

    internal static void RegisterRuntimeState(
        Func<long> messageIngressDepth,
        Func<long> messageGapIngressDepth,
        Func<long> agentIngressDepth,
        Func<long> welcomeIngressDepth,
        Func<long> outboxInFlight,
        Func<long> agentPendingWriters,
        Func<long> welcomePendingWriters)
    {
        ArgumentNullException.ThrowIfNull(messageIngressDepth);
        ArgumentNullException.ThrowIfNull(messageGapIngressDepth);
        ArgumentNullException.ThrowIfNull(agentIngressDepth);
        ArgumentNullException.ThrowIfNull(welcomeIngressDepth);
        ArgumentNullException.ThrowIfNull(outboxInFlight);
        ArgumentNullException.ThrowIfNull(agentPendingWriters);
        ArgumentNullException.ThrowIfNull(welcomePendingWriters);

        Volatile.Write(ref _messageIngressDepth, messageIngressDepth);
        Volatile.Write(ref _messageGapIngressDepth, messageGapIngressDepth);
        Volatile.Write(ref _agentIngressDepth, agentIngressDepth);
        Volatile.Write(ref _welcomeIngressDepth, welcomeIngressDepth);
        Volatile.Write(ref _outboxInFlight, outboxInFlight);
        Volatile.Write(ref _agentPendingWriters, agentPendingWriters);
        Volatile.Write(ref _welcomePendingWriters, welcomePendingWriters);
    }

    private static long Read(Func<long>? provider)
        => provider is null ? 0 : provider();
}
