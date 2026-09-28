using System.Diagnostics.Metrics;

namespace Monze;

internal static class MonzeMetrics
{
    private static readonly Meter Meter = new("Monze", "1.0");

    private static Func<long>? _messageIngressDepth;
    private static Func<long>? _agentIngressDepth;
    private static Func<long>? _welcomeIngressDepth;
    private static Func<long>? _outboxInFlight;

    static MonzeMetrics()
    {
        Meter.CreateObservableGauge(
            "monze.ingress.message.depth",
            static () => Read(_messageIngressDepth),
            "items",
            "Current message ingress queue depth.");
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
    }

    internal static readonly Counter<long> MessageIngressDropped =
        Meter.CreateCounter<long>("monze.ingress.message.dropped");

    internal static readonly Counter<long> AgentIngressBackpressure =
        Meter.CreateCounter<long>("monze.ingress.agent.backpressure");

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

    internal static void RegisterRuntimeState(
        Func<long> messageIngressDepth,
        Func<long> agentIngressDepth,
        Func<long> welcomeIngressDepth,
        Func<long> outboxInFlight)
    {
        ArgumentNullException.ThrowIfNull(messageIngressDepth);
        ArgumentNullException.ThrowIfNull(agentIngressDepth);
        ArgumentNullException.ThrowIfNull(welcomeIngressDepth);
        ArgumentNullException.ThrowIfNull(outboxInFlight);

        Volatile.Write(ref _messageIngressDepth, messageIngressDepth);
        Volatile.Write(ref _agentIngressDepth, agentIngressDepth);
        Volatile.Write(ref _welcomeIngressDepth, welcomeIngressDepth);
        Volatile.Write(ref _outboxInFlight, outboxInFlight);
    }

    private static long Read(Func<long>? provider)
        => provider is null ? 0 : provider();
}
