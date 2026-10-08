namespace Monze.Simulator;

/// <summary>
/// What a recorded <see cref="SimAction"/> was. Everything except
/// <see cref="Push"/> and <see cref="ServerClose"/> is an outbound action of
/// the bot; those two are logged as well so the order of platform events and
/// bot reactions can be read from one log.
/// </summary>
public enum SimActionKind
{
    /// <summary>REST login (POST /v2/apps/authenticate/token).</summary>
    Authenticate,

    /// <summary>Socket handshake.</summary>
    Connect,

    /// <summary>Client-initiated socket close.</summary>
    Disconnect,

    /// <summary>Ping envelope or heartbeat frame.</summary>
    Heartbeat,

    /// <summary>Read-only socket API call (ListClanDescs, ListChannelDetail, ...).</summary>
    ApiRead,

    /// <summary>Realtime ClanJoin.</summary>
    ClanJoin,

    /// <summary>Realtime ChannelJoin.</summary>
    ChannelJoin,

    /// <summary>Realtime ChannelLeave.</summary>
    ChannelLeave,

    /// <summary>Public channel message or reply (ChannelMessageSend).</summary>
    SendMessage,

    /// <summary>New ephemeral message (EphemeralMessageSend, code 12).</summary>
    SendEphemeral,

    /// <summary>Ephemeral update (EphemeralMessageSend, code 14).</summary>
    UpdateEphemeral,

    /// <summary>Ephemeral delete (EphemeralMessageSend, code 15).</summary>
    DeleteEphemeral,

    /// <summary>Message edit (UpdateChannelMessage API).</summary>
    UpdateMessage,

    /// <summary>Message removal (ChannelMessageRemove).</summary>
    DeleteMessage,

    /// <summary>Role holders changed (UpdateRole API).</summary>
    RoleAssignment,

    /// <summary>Session refresh over the socket.</summary>
    SessionRefresh,

    /// <summary>Inbound platform event pushed to the bot (not an outbound action).</summary>
    Push,

    /// <summary>The simulated server closed the socket (not an outbound action).</summary>
    ServerClose
}
