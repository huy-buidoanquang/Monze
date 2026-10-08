// Offline Mezon platform for driving the real Monze host end to end.
//
// How it plugs in: Monze builds MezonClientOptions from configuration and
// then applies a registered MonzeClientCustomization (Hosting/MonzeBot.cs).
// MezonSimulator.Customization installs MezonClientOptions.RestClientProvider
// (SimRestClient) and NetworkTransportProvider (SimTransporter), points Host
// at a host the simulator owns, turns MMN/ZK off and raises the client-side
// transport rate limits so they never delay a test.
//
// Wire facts relied on (Mezon.Net tag v1.6.2):
// - Login: SessionManager POSTs JSON {"account":{"appid","token"}} to
//   /v2/apps/authenticate/token with Basic auth (server key) and parses a
//   protobuf mezon.api.Session (Clients/MezonApiClient.cs,
//   Session/SessionManager.cs). Session.cs decodes token and refresh token as
//   JWTs ("exp", "uid", "usn"); the socket handshake token is session_id.
// - Socket API: an Api frame whose payload is Envelope{cid, api_request_event
//   {api_index (MezonApiMap), api_name, body}}; the reply is an Api frame
//   (cid, status code, body) where a non-zero code becomes MezonApiException
//   (Clients/MezonSocketClient.cs SendApiAsync / NetworkTransporter_MessageReceived).
// - Realtime: Realtime frames carry an Envelope; awaited acks are envelopes
//   with the same cid (SendRtAwaitAckAsync). Pushes have cid 0 and are
//   dispatched by Envelope case (MezonClient.EventHandling.cs).
// - Heartbeat: Ping envelope answered by Pong with the same cid (WebSocket),
//   or a Heartbeat frame (TCP). A missing pong makes the SDK reconnect.
// - Reconnect: Closed(null) from the transporter makes SocketConnectionManager
//   raise Disconnected and Reconnecting, wait about a second and reconnect on
//   the same transporter; the SDK and Monze then rejoin clans.
//
// Modelled: REST login; socket connect/disconnect/server close; heartbeat;
// ListClanDescs, ListChannelDescs (clan channels; channel type 0/1 lists all,
// clan 0 lists bot DM channels), ListChannelDetail, ListClanUsers, ListChannelMessages
// (direction 1 = anchor and newer), ListChannelVoiceUsers (occupied rooms),
// ListRoles, UpdateRole (role holders only), UpdateChannelMessage,
// SessionRefresh; ClanJoin (gates clan-scoped pushes), ChannelJoin/Leave
// (recorded), ChannelMessageSend (stored, acked, echoed to the clan stream),
// EphemeralMessageSend codes 12/14/15, ChannelMessageRemove; pushes for
// ChannelMessage (clan or DM), MessageButtonClicked (honest or forged, as
// mezon-api forwards it unchanged), DropdownBoxSelected, AddClanUserEvent,
// Voice joined/leaved/ended and Channel created/updated/deleted; faults
// (delay, error, dropped ack, socket close, refused handshake, duplicate,
// reordered and dropped pushes).
//
// Not modelled (fails closed or is refused): every other socket API and
// realtime envelope (recorded in SimRecorder.UnmodelledCalls and answered
// with Unimplemented or not at all); Agent SSE (own HttpClient, Configure
// refuses a non-empty AgentEventUrl); group DMs and the echo of bot DM sends; role metadata and
// permission edits; channel permissions beyond clan membership; paging
// cursors; echo of message updates and deletes; MMN/ZK.
using System.Security.Cryptography;
using Google.Protobuf;
using Mezon.Net.Core;
using Mezon.Net.Internal.Api;
using Mezon.Net.Internal.Realtime;
using Mezon.Net.Sdk;
using ChannelDescription = Mezon.Net.Internal.Api.ChannelDescription;

namespace Monze.Simulator;

/// <summary>
/// Façade of the offline Mezon platform: owns the <see cref="SimWorld"/>, the
/// <see cref="SimRecorder"/>, the <see cref="SimFaultPlan"/>, the inbound
/// event API (<see cref="Inbound"/>) and every simulated socket. Register
/// <see cref="Customization"/> in DI so Monze's MezonClient talks to it, or
/// call <see cref="Configure"/> on options of a probe client.
/// </summary>
public sealed class MezonSimulator : IAsyncDisposable
{
    private const int TransportRequestsPerSecond = 10_000;
    private const int TransportRequestsPerMinute = 100_000;
    private readonly object _gate = new();
    private readonly List<SimTransporter> _transporters = [];
    private readonly Dictionary<string, string> _refreshTokens = new(StringComparer.Ordinal);
    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(32);
    private readonly SemaphoreSlim _pushGate = new(1, 1);
    private readonly List<HeldPush> _held = [];
    private int _nextSessionId;
    private long _issuedSessions;

    public MezonSimulator(SimWorld world, MezonSimulatorOptions? options = null)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        Options = options ?? new MezonSimulatorOptions();
        Recorder = new SimRecorder(world.Time);
        Faults = new SimFaultPlan();
        Inbound = new SimInbound(this);
        Customization = new MonzeClientCustomization(Configure);
    }

    public SimWorld World { get; }

    public MezonSimulatorOptions Options { get; }

    public SimRecorder Recorder { get; }

    public SimFaultPlan Faults { get; }

    /// <summary>Platform events pushed to the bot (messages, clicks, joins, voice, channels, socket close).</summary>
    public SimInbound Inbound { get; }

    /// <summary>Register this singleton in Monze's DI to route the bot through the simulator.</summary>
    public MonzeClientCustomization Customization { get; }

    /// <summary>The server key the last configured client authenticates with.</summary>
    public string ServerKey { get; private set; } = MezonOptions.DefaultServerKey;

    /// <summary>Every simulated socket created so far (one per MezonClient).</summary>
    public IReadOnlyList<SimTransporter> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _transporters.ToList();
            }
        }
    }

    public IReadOnlyList<SimTransporter> ConnectedSessions => Sessions.Where(static session => session.IsConnected).ToList();

    /// <summary>Points the SDK at the simulator. Refuses options that would leave the process.</summary>
    public void Configure(MezonClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.AgentEventUrl))
        {
            throw new InvalidOperationException(
                "Agent SSE is not modelled by the Monze simulator (it uses its own HttpClient). Leave Mezon:AgentBaseUrl empty.");
        }

        options.Host = Options.Host;
        options.Port = Options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        options.UseSSL = false;
        options.MMNApiUrl = string.Empty;
        options.ZkApiUrl = string.Empty;
        options.RestClientProvider = baseUrl => new SimRestClient(this, baseUrl);
        options.NetworkTransportProvider = CreateTransporter;
        options.MaxTransportRequestsPerSecond = TransportRequestsPerSecond;
        options.MaxTransportRequestsPerMinute = TransportRequestsPerMinute;
        options.MaxConnectRequestsPerSecond = TransportRequestsPerSecond;
        if (Options.HeartbeatIntervalMilliseconds is int heartbeat)
        {
            options.HeartbeatIntervalInMilliseconds = heartbeat;
        }

        if (Options.SocketTimeoutMilliseconds is int timeout)
        {
            options.SocketTimeoutInMilliseconds = timeout;
        }

        ServerKey = options.ServerKey;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in Sessions)
        {
            await session.CloseFromServerAsync("simulator disposed").ConfigureAwait(false);
            session.Dispose();
        }

        _pushGate.Dispose();
    }

    internal static string MeetingCode(SimChannel channel)
        => channel.IsVoice ? $"sim-room-{channel.Id}" : string.Empty;

    internal ChannelDescription ToDescription(SimChannel channel)
        => new()
        {
            ClanId = channel.ClanId,
            ChannelId = channel.Id,
            ParentId = channel.ParentId,
            CategoryId = channel.CategoryId,
            Type = channel.Type,
            ChannelLabel = channel.Label,
            ChannelPrivate = channel.IsPrivate ? 1 : 0,
            CreatorId = channel.CreatorId,
            ClanName = World.FindClan(channel.ClanId)?.Name ?? string.Empty,
            MeetingCode = MeetingCode(channel),
            Active = 1
        };

    /// <summary>Builds the api.ChannelMessage the platform pushes and lists for a stored message.</summary>
    internal ChannelMessage ToChannelMessage(SimMessage message)
    {
        var sender = World.FindUser(message.SenderId);
        var member = World.FindMember(message.ClanId, message.SenderId);
        var channel = World.FindChannel(message.ChannelId);
        var proto = new ChannelMessage
        {
            ClanId = message.ClanId,
            ChannelId = message.ChannelId,
            MessageId = message.Id,
            Code = message.Code,
            SenderId = message.SenderId,
            Username = sender?.Username ?? string.Empty,
            DisplayName = sender?.DisplayName ?? string.Empty,
            Avatar = sender?.Avatar ?? string.Empty,
            ClanNick = member?.ClanNick ?? string.Empty,
            Content = message.ContentJson,
            ChannelLabel = channel?.Label ?? string.Empty,
            CreateTimeSeconds = (uint)message.CreatedAt.ToUnixTimeSeconds(),
            UpdateTimeSeconds = (uint)message.CreatedAt.ToUnixTimeSeconds(),
            Mode = channel?.StreamMode ?? 2,
            IsPublic = channel is not null && !channel.IsPrivate,
            HideEditted = true
        };
        if (message.MentionUserIds.Count > 0)
        {
            var mentions = new MessageMentionList();
            foreach (var userId in message.MentionUserIds)
            {
                mentions.Mentions.Add(new MessageMention { UserId = userId, Username = World.FindUser(userId)?.Username ?? string.Empty });
            }

            proto.Mentions = mentions.ToByteString();
        }

        if (message.ReplyToMessageId is long replyTo && World.FindMessage(replyTo) is { } original)
        {
            var refs = new MessageRefList();
            refs.Refs.Add(new MessageRef
            {
                MessageId = message.Id,
                MessageRefId = replyTo,
                RefType = 0,
                Content = original.ContentJson,
                MessageSenderId = original.SenderId,
                MessageSenderUsername = World.FindUser(original.SenderId)?.Username ?? string.Empty
            });
            proto.References = refs.ToByteString();
        }

        return proto;
    }

    internal Session IssueSession()
    {
        var bot = World.Bot;
        var number = Interlocked.Increment(ref _issuedSessions);
        var sessionId = $"sim-session-{number}";
        var session = CreateSession(bot, sessionId);
        lock (_gate)
        {
            _refreshTokens[sessionId] = session.RefreshToken;
        }

        return session;
    }

    internal Session? RefreshSession(string refreshToken)
    {
        lock (_gate)
        {
            foreach (var (sessionId, token) in _refreshTokens)
            {
                if (string.Equals(token, refreshToken, StringComparison.Ordinal))
                {
                    var session = CreateSession(World.Bot, sessionId);
                    _refreshTokens[sessionId] = session.RefreshToken;
                    return session;
                }
            }
        }

        return null;
    }

    internal bool IsSessionToken(string token)
    {
        lock (_gate)
        {
            return _refreshTokens.ContainsKey(token);
        }
    }

    /// <summary>Pushes an envelope through the push fault plan to the eligible sessions.</summary>
    internal async Task<SimPush> PushAsync(SimPushKind kind, Envelope envelope, Func<SimTransporter, bool> eligible, SimAction draft)
    {
        var payload = envelope.ToByteArray();
        await _pushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var fault = Faults.TakePush(kind);
            var targets = ConnectedSessions.Where(eligible).ToList();
            var action = Recorder.Record(draft with
            {
                Kind = SimActionKind.Push,
                Operation = kind.ToString(),
                Sessions = targets.Count,
                Fault = fault?.Kind,
                Request = envelope
            });
            switch (fault?.Kind)
            {
                case SimFaultKind.DropPush:
                    return new SimPush(action.Sequence, kind, targets.Count, 0, draft.MessageId, fault.Kind);
                case SimFaultKind.ReorderPush:
                    _held.Add(new HeldPush(kind, payload, eligible, draft with { Request = envelope }));
                    return new SimPush(action.Sequence, kind, targets.Count, 0, draft.MessageId, fault.Kind);
            }

            var delivered = await DeliverAsync(payload, targets, fault?.Kind == SimFaultKind.DuplicatePush ? 2 : 1).ConfigureAwait(false);
            await ReleaseHeldLockedAsync().ConfigureAwait(false);
            return new SimPush(action.Sequence, kind, targets.Count, delivered, draft.MessageId, fault?.Kind);
        }
        finally
        {
            _pushGate.Release();
        }
    }

    /// <summary>Delivers pushes held by a reorder fault.</summary>
    internal async Task<int> ReleaseHeldAsync()
    {
        await _pushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ReleaseHeldLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _pushGate.Release();
        }
    }

    /// <summary>Broadcasts the bot's own message to every bot session in the clan (no faults).</summary>
    internal void Echo(SimMessage message)
    {
        var envelope = new Envelope { ChannelMessage = ToChannelMessage(message) };
        var targets = ConnectedSessions.Where(session => session.HasJoinedClan(message.ClanId)).ToList();
        Recorder.Record(new SimAction
        {
            Kind = SimActionKind.Push,
            Operation = SimPushKind.ChannelMessage.ToString(),
            ClanId = message.ClanId,
            ChannelId = message.ChannelId,
            MessageId = message.Id,
            TargetUserId = message.SenderId,
            ContentJson = message.ContentJson,
            Sessions = targets.Count,
            Request = envelope
        });
        var payload = envelope.ToByteArray();
        foreach (var target in targets)
        {
            _ = target.EnqueuePush(payload);
        }
    }

    private async Task<int> ReleaseHeldLockedAsync()
    {
        if (_held.Count == 0)
        {
            return 0;
        }

        var held = _held.ToList();
        _held.Clear();
        var released = 0;
        foreach (var push in held)
        {
            var targets = ConnectedSessions.Where(push.Eligible).ToList();
            Recorder.Record(push.Draft with
            {
                Kind = SimActionKind.Push,
                Operation = push.Kind.ToString(),
                Sessions = targets.Count,
                Fault = SimFaultKind.ReorderPush
            });
            released += await DeliverAsync(push.Payload, targets, 1).ConfigureAwait(false);
        }

        return released;
    }

    private static async Task<int> DeliverAsync(byte[] payload, IReadOnlyList<SimTransporter> targets, int copies)
    {
        var deliveries = new List<Task<bool>>(targets.Count);
        foreach (var target in targets)
        {
            var first = target.EnqueuePush(payload);
            for (var copy = 1; copy < copies; copy++)
            {
                _ = target.EnqueuePush(payload);
            }

            deliveries.Add(first);
        }

        var results = await Task.WhenAll(deliveries).ConfigureAwait(false);
        return results.Count(static delivered => delivered);
    }

    private Session CreateSession(SimBotIdentity bot, string sessionId)
    {
        var now = World.Time.GetUtcNow();
        var token = SimJwt.Create(
            new Dictionary<string, object>
            {
                ["uid"] = bot.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["usn"] = bot.Username,
                ["sid"] = sessionId,
                ["exp"] = now.Add(Options.SessionLifetime).ToUnixTimeSeconds()
            },
            _signingKey);
        var refresh = SimJwt.Create(
            new Dictionary<string, object>
            {
                ["uid"] = bot.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sid"] = sessionId,
                ["nonce"] = Guid.NewGuid().ToString("N"),
                ["exp"] = now.Add(Options.SessionLifetime * 7).ToUnixTimeSeconds()
            },
            _signingKey);
        return new Session
        {
            Created = false,
            Token = token,
            RefreshToken = refresh,
            UserId = bot.Id,
            SessionId = sessionId
        };
    }

    private SimTransporter CreateTransporter(TransportType transportType)
    {
        lock (_gate)
        {
            var transporter = new SimTransporter(this, transportType, ++_nextSessionId);
            _transporters.Add(transporter);
            return transporter;
        }
    }

    private sealed record HeldPush(SimPushKind Kind, byte[] Payload, Func<SimTransporter, bool> Eligible, SimAction Draft);
}
