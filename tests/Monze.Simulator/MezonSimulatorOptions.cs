namespace Monze.Simulator;

/// <summary>
/// Knobs of the simulated platform. Defaults mirror the behaviour observed
/// in mezon-api: the bot's own channel messages are broadcast back to its
/// clan stream, sessions last a day, and the SDK keeps its own heartbeat and
/// socket timeouts unless overridden here.
/// </summary>
public sealed class MezonSimulatorOptions
{
    /// <summary>Gateway host the customization installs; any other host fails closed.</summary>
    public string Host { get; init; } = "mezon.simulator.invalid";

    public int Port { get; init; } = 7350;

    /// <summary>
    /// Push the bot's own ChannelMessageSend back as a ChannelMessage to every
    /// bot session that joined the clan (mezon-api channelMessageSend
    /// dispatches to the whole clan stream). Ephemeral messages are never echoed.
    /// </summary>
    public bool EchoBotMessages { get; init; } = true;

    /// <summary>Overrides MezonSocketClientOptions.HeartbeatIntervalInMilliseconds when set.</summary>
    public int? HeartbeatIntervalMilliseconds { get; init; }

    /// <summary>Overrides MezonOptions.SocketTimeoutInMilliseconds when set (dropped-ack tests).</summary>
    public int? SocketTimeoutMilliseconds { get; init; }

    /// <summary>
    /// Keep the client-side transport rate limits Monze configured (the
    /// production behaviour, load variant V1) instead of raising them so they
    /// never delay a test.
    /// </summary>
    public bool KeepClientRateLimits { get; init; }

    /// <summary>
    /// Extra time before the platform answers a socket API call or realtime
    /// envelope (not heartbeats), e.g. a log-normal ack latency for load
    /// variant V3L. Called once per request; Windows timers round it up to
    /// about 15 ms steps.
    /// </summary>
    public Func<string, TimeSpan>? ResponseLatency { get; init; }

    /// <summary>
    /// Most clans ListClanDescs returns; the current Mezon discovery response
    /// is capped at 100 entries (docs/capacity-gate.md). Null lifts the cap.
    /// </summary>
    public int? ClanDiscoveryLimit { get; init; } = 100;

    /// <summary>Lifetime of issued session tokens.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Gateway base URL the SDK derives (MezonOptions.GatewayBasePath with UseSSL false).</summary>
    public string GatewayBasePath => $"http://{Host}:{Port}";
}
