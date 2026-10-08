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

    /// <summary>Lifetime of issued session tokens.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Gateway base URL the SDK derives (MezonOptions.GatewayBasePath with UseSSL false).</summary>
    public string GatewayBasePath => $"http://{Host}:{Port}";
}
