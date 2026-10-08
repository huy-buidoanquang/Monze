using System.Collections.Concurrent;
using System.Threading.Channels;
using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Caching.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Application.Ingress;
using Monze.Domain;
using Monze.Hosting;
using System.Text.Json;

namespace Monze;

public sealed partial class MonzeBot : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly MonzeApp _app;
    private readonly IClanRegistryRepository _clans;
    private readonly IAuthorizationRepository _authorization;
    private readonly IWelcomeRepository _welcome;
    private readonly IMeetingRepository _meeting;
    private readonly IScheduledMeetingRepository _scheduledMeeting;
    private readonly ISchedulingRepository _scheduling;
    private readonly IOutboxRepository _outbox;
    private readonly IMessageHistoryRepository _messageHistory;
    private readonly IUserProfileRepository _userProfiles;
    private readonly ICommandInboxRepository _commandInbox;
    private readonly IInteractionInboxRepository _interactionInbox;
    private readonly IWelcomeSetupDraftStore _welcomeSetupDrafts;
    private readonly ITranscriptClient _transcript;
    private readonly MeetingSummaryComposer _summaryComposer;
    private readonly IReadModelCache _readModelCache;
    private readonly IMemoryCache _policyCache;
    private readonly ILogger<MonzeBot> _logger;
    private readonly MonzeCommandOptions _commandOptions;
    private readonly MonzeConnectionRetryOptions _connectionRetryOptions;
    private readonly MonzeCommandRateLimiter _commandRateLimiter;
    private readonly StartupReadiness _readiness;
    private readonly TimeProvider _time;
    private readonly MonzeWorkerTimings _timings;
    private readonly MonzeClientCustomization? _clientCustomization;
    private readonly int _maxCommandsInFlight;
    private readonly IEventIngressQueue<ChannelMessageEventData> _messageIngress;
    private readonly Channel<MessageGapIngressItem> _messageGapIngress;
    private readonly Channel<MeetingIngressItem> _meetingIngress;
    private readonly PartitionedIngressQueue<WelcomeIngressItem> _welcomeIngress;
    private readonly ConcurrentDictionary<VoiceKey, int> _voiceOccupancy = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, byte>> _voiceChannelsByClan = new();
    private readonly ConcurrentDictionary<long, long> _voiceClanByChannel = new();
    private readonly ConcurrentDictionary<long, long> _welcomeChannelIds = new();
    private readonly VoiceSnapshotGuard _voiceSnapshots = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _voiceSelectionGates = new();
    private readonly SemaphoreSlim _clanJoinGate = new(1, 1);
    private IReadOnlyList<KnownClan> _knownClans = Array.Empty<KnownClan>();
    private HashSet<long> _knownClanIds = [];
    private CancellationToken _runtimeToken;
    private long _droppedMessages;
    private long _droppedMessageGaps;
    private long _messageIngressDepth;
    private long _messageGapIngressDepth;
    private long _agentIngressDepth;
    private long _welcomeIngressDepth;
    private long _agentPendingWriters;
    private long _welcomePendingWriters;
    private long _outboxInFlight;
    private int _commandsInFlight;
    private SqliteMessageStore? _messages;

    public MonzeBot(
        IConfiguration configuration,
        MonzeApp app,
        IClanRegistryRepository clans,
        IAuthorizationRepository authorization,
        IWelcomeRepository welcome,
        IMeetingRepository meeting,
        IScheduledMeetingRepository scheduledMeeting,
        ISchedulingRepository scheduling,
        IOutboxRepository outbox,
        IMessageHistoryRepository messageHistory,
        IUserProfileRepository userProfiles,
        ICommandInboxRepository commandInbox,
        IInteractionInboxRepository interactionInbox,
        IWelcomeSetupDraftStore welcomeSetupDrafts,
        ITranscriptClient transcript,
        MeetingSummaryComposer summaryComposer,
        IReadModelCache readModelCache,
        IMemoryCache policyCache,
        MonzeCommandOptions commandOptions,
        MonzeConnectionRetryOptions connectionRetryOptions,
        MonzeCommandRateLimiter commandRateLimiter,
        StartupReadiness readiness,
        TimeProvider time,
        MonzeWorkerTimings timings,
        ILogger<MonzeBot> logger,
        MonzeClientCustomization? clientCustomization = null)
    {
        _configuration = configuration;
        _app = app;
        _clans = clans;
        _authorization = authorization;
        _welcome = welcome;
        _meeting = meeting;
        _scheduledMeeting = scheduledMeeting;
        _scheduling = scheduling;
        _outbox = outbox;
        _messageHistory = messageHistory;
        _userProfiles = userProfiles;
        _commandInbox = commandInbox;
        _interactionInbox = interactionInbox;
        _welcomeSetupDrafts = welcomeSetupDrafts;
        _transcript = transcript;
        _summaryComposer = summaryComposer;
        _readModelCache = readModelCache;
        _policyCache = policyCache;
        _commandOptions = commandOptions;
        _connectionRetryOptions = connectionRetryOptions;
        _commandRateLimiter = commandRateLimiter;
        _readiness = readiness;
        _time = time;
        _timings = timings;
        _logger = logger;
        _clientCustomization = clientCustomization;
        _maxCommandsInFlight = Math.Clamp(configuration.GetValue("Monze:Commands:MaxInFlight", 64), 1, 4096);
        var weakSelf = new WeakReference<MonzeBot>(this);
        MonzeMetrics.RegisterRuntimeState(
            () => weakSelf.TryGetTarget(out var bot)
                ? Volatile.Read(ref bot._messageIngressDepth)
                : 0,
            () => weakSelf.TryGetTarget(out var bot)
                ? Volatile.Read(ref bot._messageGapIngressDepth)
                : 0,
            () => weakSelf.TryGetTarget(out var bot)
                ? Volatile.Read(ref bot._agentIngressDepth)
                : 0,
            () => weakSelf.TryGetTarget(out var bot)
                ? Volatile.Read(ref bot._welcomeIngressDepth)
                : 0,
            () => weakSelf.TryGetTarget(out var bot)
                ? Volatile.Read(ref bot._outboxInFlight)
                : 0,
            () => weakSelf.TryGetTarget(out var bot)
                ? Volatile.Read(ref bot._agentPendingWriters)
                : 0,
            () => weakSelf.TryGetTarget(out var bot)
                ? Volatile.Read(ref bot._welcomePendingWriters)
                : 0);
        var redisCache = _readModelCache as Monze.Infrastructure.Caching.MonzeReadModelCache;
        _logger.LogInformation(
            "Read model cache selected: {CacheType}, RedisConnected={RedisConnected}",
            _readModelCache.GetType().Name,
            redisCache?.IsConnected ?? false);

        var messageCapacity = Math.Clamp(
            configuration.GetValue("Monze:Queues:MessageCapacity", 8192),
            1024,
            65536);
        var messagePartitions = NormalizePartitionCount(
            configuration.GetValue("Monze:Queues:MessagePartitions", 16));
        var agentCapacity = Math.Clamp(
            configuration.GetValue("Monze:Queues:AgentCapacity", 2048),
            128,
            16384);
        _messageIngress = new PartitionedIngressQueue<ChannelMessageEventData>(
            messagePartitions,
            messageCapacity);
        _messageGapIngress = Channel.CreateBounded<MessageGapIngressItem>(
            new BoundedChannelOptions(Math.Clamp(messageCapacity / 4, 256, 4096))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        _logger.LogInformation(
            "Message ingress configured: Partitions={Partitions}, Capacity={Capacity}.",
            _messageIngress.PartitionCount,
            _messageIngress.Capacity);
        _meetingIngress = Channel.CreateBounded<MeetingIngressItem>(
            new BoundedChannelOptions(agentCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        // One lane per clan hash: a slow welcome delays only its own lane (DEF-04).
        _welcomeIngress = new PartitionedIngressQueue<WelcomeIngressItem>(WelcomePartitions, 1024);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _readiness.Ready.WaitAsync(stoppingToken);
        var botId = _configuration.GetValue<long>("Mezon:BotId");
        var token = _configuration["Mezon:Token"];
        if (botId == 0 || string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("Mezon bot credentials are empty. Host stays up after schema validation only.");
            await Task.Delay(Timeout.Infinite, stoppingToken);
            return;
        }

        var directory = _configuration["Monze:SqliteDirectory"] ?? "data";
        Directory.CreateDirectory(directory);
        var path = SqliteMessageStorePaths.ResolveDatabasePath(
            directory,
            botId.ToString(),
            _configuration["Monze:EnvironmentName"] ?? "dev");
        _messages = await SqliteMessageStore.OpenAsync(path, stoppingToken);

        var options = CreateClientOptions(_configuration, botId, token, _clientCustomization);
        await using var client = new MezonClient(options);
        _app.AttachRoleGateway(new SdkRoleGateway(client, _logger));
        ConfigureCommands(client);
        ConfigureInteractions(client);
        SubscribeRealtimeEvents(client);

        using var runtimeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var runtimeToken = runtimeCts.Token;
        _runtimeToken = runtimeToken;
        SubscribeConnectionEvents(client);
        var messageWorker = ConsumeMessagesAsync(runtimeToken);
        var messageGapWorker = ConsumeMessageGapsAsync(runtimeToken);
        var agentWorker = ConsumeAgentEventsAsync(client, runtimeToken);
        var welcomeWorker = ConsumeWelcomeAsync(client, runtimeToken);
        var roleWorker = Task.CompletedTask;
        var outboxWorker = Task.CompletedTask;
        AgentEventStream? agentEvents = null;

        try
        {
            await LoginWithRetryAsync(client, runtimeToken);
            await RunStartupOperationWithRetryAsync(
                () => RefreshClansAsync(client, runtimeToken),
                "clan discovery",
                runtimeToken);
            if (!string.IsNullOrWhiteSpace(options.AgentEventUrl))
            {
                agentEvents = new AgentEventStream(
                    options.AgentEventUrl,
                    botId,
                    token,
                    TimeSpan.FromSeconds(Math.Clamp(_configuration.GetValue("Mezon:AgentSse:IdleTimeoutSeconds", 45), 1, 3600)),
                    _time,
                    _logger,
                    RouteAgentEventAsync);
                await agentEvents.ConnectAsync(runtimeToken);
            }

            outboxWorker = RunOutboxWorkerAsync(client, CreateOutboxPacer(_configuration, options, _time), runtimeToken);
            roleWorker = ConsumeAutomaticRoleRulesAsync(runtimeToken);

            while (!runtimeToken.IsCancellationRequested)
            {
                try
                {
                    await FlushScheduledMeetingsAsync(client, runtimeToken);
                }
                catch (OperationCanceledException) when (runtimeToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Monze worker iteration failed; retrying.");
                }

                await Task.Delay(_timings.SchedulerInterval, _time, runtimeToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monze runtime failed after startup.");
            throw;
        }
        finally
        {
            client.ChannelMessageReceived -= EnqueueMessageAsync;
            runtimeCts.Cancel();
            if (agentEvents is not null)
            {
                await agentEvents.DisposeAsync();
            }

            _messageIngress.Complete();
            _meetingIngress.Writer.TryComplete();
            _welcomeIngress.Complete();
            try
            {
                await Task.WhenAll(
                    messageWorker,
                    messageGapWorker,
                    agentWorker,
                    welcomeWorker,
                    roleWorker,
                    outboxWorker);
            }
            catch (OperationCanceledException) when (runtimeToken.IsCancellationRequested)
            {
            }
            finally
            {
                _messageGapIngress.Writer.TryComplete();
            }
        }
    }

    internal static MezonClientOptions CreateClientOptions(
        IConfiguration configuration,
        long botId,
        string token,
        MonzeClientCustomization? customization)
    {
        var perMinute = Math.Clamp(
            configuration.GetValue("Mezon:RateLimit:RequestsPerMinute", 500),
            1,
            10000);
        var options = new MezonClientOptions(
            botId,
            token,
            configuration["Mezon:Host"] ?? "gw.mezon.ai",
            configuration["Mezon:Port"] ?? "443",
            configuration.GetValue("Mezon:UseSsl", true))
        {
            TransportType = ResolveTransportType(configuration["Mezon:Transport"]),
            AgentEventUrl = configuration["Mezon:AgentBaseUrl"] ?? string.Empty,

            // Paced evenly by default: a sixtieth of the minute budget per
            // second never spends the minute window early. With the SDK's
            // 60/s, 500 requests went out in about eight seconds and nothing
            // was sent for the rest of the minute.
            MaxTransportRequestsPerSecond = Math.Clamp(
                configuration.GetValue("Mezon:RateLimit:RequestsPerSecond", Math.Max(1, perMinute / 60)),
                1,
                1000),
            MaxTransportRequestsPerMinute = perMinute,
            MaxConnectRequestsPerSecond = Math.Clamp(
                configuration.GetValue("Mezon:RateLimit:ConnectRequestsPerSecond", 2),
                1,
                100),
            SocketHandlerTimeoutInMilliseconds = null,
            DefaultRatelimitCallback = MonzeMetrics.RecordUpstreamRateLimit
        };
        customization?.Configure(options);
        return options;
    }

    /// <summary>
    /// The pace of bulk outbox delivery: at most
    /// Monze:Outbox:TransportSharePercent (default 50) of the transport's
    /// minute budget, so command replies, buttons and welcome messages keep
    /// the rest while a backlog drains. It banks about two seconds of sends.
    /// </summary>
    internal static UpstreamPacer CreateOutboxPacer(IConfiguration configuration, MezonClientOptions options, TimeProvider time)
    {
        var share = Math.Clamp(configuration.GetValue("Monze:Outbox:TransportSharePercent", 50), 1, 100);
        var perMinute = options.MaxTransportRequestsPerMinute * share / 100.0;
        return new UpstreamPacer(perMinute, Math.Clamp((int)(perMinute / 30), 1, 64), time);
    }

    private static TransportType ResolveTransportType(string? configured)
        => string.Equals(configured, "Tcp", StringComparison.OrdinalIgnoreCase)
            ? TransportType.Tcp
            : TransportType.WebSocket;

    private static int NormalizePartitionCount(int configured)
    {
        var bounded = Math.Clamp(configured, 1, 64);
        var normalized = 1;
        while (normalized < bounded)
        {
            normalized <<= 1;
        }

        return normalized;
    }

    public override void Dispose()
    {
        foreach (var gate in _voiceSelectionGates.Values)
        {
            gate.Dispose();
        }

        _clanJoinGate.Dispose();
        base.Dispose();
    }
}

