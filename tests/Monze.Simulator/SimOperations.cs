namespace Monze.Simulator;

/// <summary>
/// Names of the wire operations the simulator distinguishes. Socket API
/// names are the <c>ApiRequestEvent.api_name</c> values of Mezon.Net.Sdk
/// 1.6.2 (src/Mezon.Net.Client/Clients/MezonSocketClient.cs); realtime names
/// are the <c>Envelope.MessageOneofCase</c> names the SDK sends
/// (src/Mezon.Net.Client/Generated/BaseMezonSocketClient.Realtime.g.cs).
/// Faults are keyed by these names.
/// </summary>
public static class SimOperations
{
    /// <summary>REST login used by SessionManager (src/Mezon.Net.Client/Clients/MezonApiClient.cs).</summary>
    public const string Authenticate = "POST /v2/apps/authenticate/token";

    /// <summary>Socket handshake (IMezonNetworkTransporter.ConnectAsync).</summary>
    public const string SocketConnect = "SocketConnect";

    /// <summary>Client-initiated close (IMezonNetworkTransporter.DisconnectAsync).</summary>
    public const string SocketDisconnect = "SocketDisconnect";

    /// <summary>Ping envelope over WebSocket or a heartbeat frame over TCP.</summary>
    public const string Heartbeat = "Heartbeat";

    public const string ListClanDescs = "ListClanDescs";
    public const string ListChannelDescs = "ListChannelDescs";
    public const string ListChannelDetail = "ListChannelDetail";
    public const string ListClanUsers = "ListClanUsers";
    public const string ListChannelMessages = "ListChannelMessages";
    public const string ListChannelVoiceUsers = "ListChannelVoiceUsers";
    public const string ListRoles = "ListRoles";
    public const string UpdateRole = "UpdateRole";
    public const string UpdateChannelMessage = "UpdateChannelMessage";
    public const string SessionRefresh = "SessionRefresh";

    public const string ClanJoin = "ClanJoin";
    public const string ChannelJoin = "ChannelJoin";
    public const string ChannelLeave = "ChannelLeave";
    public const string ChannelMessageSend = "ChannelMessageSend";
    public const string EphemeralMessageSend = "EphemeralMessageSend";
    public const string ChannelMessageRemove = "ChannelMessageRemove";

    /// <summary>
    /// Agent SSE (src/Mezon.Net.Sdk/Agent/AgentSseManager.cs) opens its own
    /// HttpClient, so it bypasses both SDK seams; <see cref="SimHttpHost"/>
    /// fakes it over loopback HTTP instead.
    /// </summary>
    public const string AgentSse = "AgentSse";

    /// <summary>Socket API names answered from the world.</summary>
    public static IReadOnlySet<string> Apis { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ListClanDescs,
        ListChannelDescs,
        ListChannelDetail,
        ListClanUsers,
        ListChannelMessages,
        ListChannelVoiceUsers,
        ListRoles,
        UpdateRole,
        UpdateChannelMessage,
        SessionRefresh
    };

    /// <summary>Realtime envelopes the simulator handles.</summary>
    public static IReadOnlySet<string> Realtime { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Heartbeat,
        ClanJoin,
        ChannelJoin,
        ChannelLeave,
        ChannelMessageSend,
        EphemeralMessageSend,
        ChannelMessageRemove
    };

    /// <summary>Every operation the simulator models; anything else fails closed.</summary>
    public static IReadOnlySet<string> Modelled { get; } = new HashSet<string>(
        Apis.Concat(Realtime).Concat([Authenticate, SocketConnect, SocketDisconnect]),
        StringComparer.Ordinal);

    /// <summary>Operations that are deliberately not modelled, with the reason.</summary>
    public static IReadOnlyDictionary<string, string> NotModelled { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AgentSse] = "Agent SSE uses its own HttpClient (no SDK seam); point Mezon:AgentBaseUrl at a SimHttpHost or keep it empty."
    };
}
