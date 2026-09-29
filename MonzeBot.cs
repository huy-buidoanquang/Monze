using System.Collections.Concurrent;
using System.Threading.Channels;
using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Agent;
using Mezon.Net.Sdk.Caching.Sqlite;
using Mezon.Net.Sdk.Commands;
using Mezon.Net.Sdk.Interactions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Application.Ingress;
using Monze.Domain;
using Monze.Hosting;
using Monze.Ui;
using System.Text.Json;

namespace Monze;

public sealed partial class MonzeBot : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly MonzeApp _app;
    private readonly IClanRegistryRepository _clans;
    private readonly IAuthorizationRepository _authorization;
    private readonly IMeetingRepository _meeting;
    private readonly IScheduledMeetingRepository _scheduledMeeting;
    private readonly ISchedulingRepository _scheduling;
    private readonly IOutboxRepository _outbox;
    private readonly IMessageHistoryRepository _messageHistory;
    private readonly ICommandInboxRepository _commandInbox;
    private readonly IWelcomeSetupDraftStore _welcomeSetupDrafts;
    private readonly ITranscriptClient _transcript;
    private readonly IReadModelCache _readModelCache;
    private readonly IMemoryCache _policyCache;
    private readonly ILogger<MonzeBot> _logger;
    private readonly MonzeCommandOptions _commandOptions;
    private readonly MonzeConnectionRetryOptions _connectionRetryOptions;
    private readonly MonzeCommandRateLimiter _commandRateLimiter;
    private readonly IEventIngressQueue<ChannelMessageEventData> _messageIngress;
    private readonly Channel<AgentIngressItem> _agentIngress;
    private readonly Channel<WelcomeIngressItem> _welcomeIngress;
    private readonly ConcurrentDictionary<VoiceKey, int> _voiceOccupancy = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, byte>> _voiceChannelsByClan = new();
    private readonly ConcurrentDictionary<long, long> _welcomeChannelIds = new();
    private readonly ConcurrentDictionary<long, byte> _voiceSnapshotReady = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _voiceSelectionGates = new();
    private readonly SemaphoreSlim _clanJoinGate = new(1, 1);
    private IReadOnlyList<KnownClan> _knownClans = Array.Empty<KnownClan>();
    private CancellationToken _runtimeToken;
    private long _droppedMessages;
    private long _messageIngressDepth;
    private long _agentIngressDepth;
    private long _welcomeIngressDepth;
    private long _outboxInFlight;
    private SqliteMessageStore? _messages;

    public MonzeBot(
        IConfiguration configuration,
        MonzeApp app,
        IClanRegistryRepository clans,
        IAuthorizationRepository authorization,
        IMeetingRepository meeting,
        IScheduledMeetingRepository scheduledMeeting,
        ISchedulingRepository scheduling,
        IOutboxRepository outbox,
        IMessageHistoryRepository messageHistory,
        ICommandInboxRepository commandInbox,
        IWelcomeSetupDraftStore welcomeSetupDrafts,
        ITranscriptClient transcript,
        IReadModelCache readModelCache,
        IMemoryCache policyCache,
        MonzeCommandOptions commandOptions,
        MonzeConnectionRetryOptions connectionRetryOptions,
        MonzeCommandRateLimiter commandRateLimiter,
        ILogger<MonzeBot> logger)
    {
        _configuration = configuration;
        _app = app;
        _clans = clans;
        _authorization = authorization;
        _meeting = meeting;
        _scheduledMeeting = scheduledMeeting;
        _scheduling = scheduling;
        _outbox = outbox;
        _messageHistory = messageHistory;
        _commandInbox = commandInbox;
        _welcomeSetupDrafts = welcomeSetupDrafts;
        _transcript = transcript;
        _readModelCache = readModelCache;
        _policyCache = policyCache;
        _commandOptions = commandOptions;
        _connectionRetryOptions = connectionRetryOptions;
        _commandRateLimiter = commandRateLimiter;
        _logger = logger;
        var weakSelf = new WeakReference<MonzeBot>(this);
        MonzeMetrics.RegisterRuntimeState(
            () => weakSelf.TryGetTarget(out var bot)
                ? Volatile.Read(ref bot._messageIngressDepth)
                : 0,
            () => weakSelf.TryGetTarget(out var bot)
                ? Volatile.Read(ref bot._agentIngressDepth)
                : 0,
            () => weakSelf.TryGetTarget(out var bot)
                ? Volatile.Read(ref bot._welcomeIngressDepth)
                : 0,
            () => weakSelf.TryGetTarget(out var bot)
                ? Volatile.Read(ref bot._outboxInFlight)
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
        _logger.LogInformation(
            "Message ingress configured: Partitions={Partitions}, Capacity={Capacity}.",
            _messageIngress.PartitionCount,
            _messageIngress.Capacity);
        _agentIngress = Channel.CreateBounded<AgentIngressItem>(
            new BoundedChannelOptions(agentCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        _welcomeIngress = Channel.CreateBounded<WelcomeIngressItem>(
            new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartupSchemaValidator.Ready.WaitAsync(stoppingToken);
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

        var options = new MezonClientOptions(
            botId,
            token,
            _configuration["Mezon:Host"] ?? "gw.mezon.ai",
            _configuration["Mezon:Port"] ?? "443",
            _configuration.GetValue("Mezon:UseSsl", true))
        {
            TransportType = ResolveTransportType(_configuration["Mezon:Transport"]),
            AgentEventUrl = _configuration["Mezon:AgentEventUrl"] ?? string.Empty,
            MaxTransportRequestsPerSecond = Math.Clamp(
                _configuration.GetValue("Mezon:RateLimit:RequestsPerSecond", 60),
                1,
                1000),
            MaxTransportRequestsPerMinute = Math.Clamp(
                _configuration.GetValue("Mezon:RateLimit:RequestsPerMinute", 500),
                1,
                10000),
            MaxConnectRequestsPerSecond = Math.Clamp(
                _configuration.GetValue("Mezon:RateLimit:ConnectRequestsPerSecond", 2),
                1,
                100),
            SocketHandlerTimeoutInMilliseconds = null
        };

        await using var client = new MezonClient(options);
        _app.AttachRoleGateway(new SdkRoleGateway(client, _logger));
        var commands = new CommandService(_commandOptions.Prefix);
        if (_commandOptions.HasRoot)
        {
            commands.AddCommand(_commandOptions.Root!, HandleMonzeAsync);
        }
        else
        {
            commands.AddCommand(MonzeCommandNames.Help, HandleDirectHelpAsync);
        }

        foreach (var module in MonzeCommandNames.DirectModules)
        {
            var directModule = module;
            commands.AddCommand(directModule, context => HandleDirectMonzeAsync(context, directModule));
        }

        commands.AddCommand(MonzeCommandNames.Meeting, HandleMeetingAsync);
        commands.AddCommand(MonzeCommandNames.Summary, HandleSummaryAsync);
        client.UseCommands(commands);

        var interactions = new InteractionRouter();
        interactions.OnButton(
            MonzeButtonId.HelpMeeting,
            ctx => HandleHelpPageAsync(ctx, "meeting"));
        interactions.OnButton(
            MonzeButtonId.HelpSummary,
            ctx => HandleHelpPageAsync(ctx, "summary"));
        interactions.OnButton(
            MonzeButtonId.HelpWelcome,
            ctx => HandleHelpPageAsync(ctx, "welcome"));
        interactions.OnButton(
            MonzeButtonId.HelpRole,
            ctx => HandleHelpPageAsync(ctx, "role"));
        interactions.OnButton(
            MonzeButtonId.HelpAi,
            ctx => HandleHelpPageAsync(ctx, "ai"));
        interactions.OnButton(
            MonzeButtonId.HelpSetup,
            ctx => HandleHelpPageAsync(ctx, "setup"));
        interactions.OnButton(
            MonzeButtonId.HelpClose,
            HandleHelpCloseAsync);
        interactions.OnButton(
            MeetingButtonId.Refresh,
            HandleMeetingListAsync);
        interactions.OnButton(
            MeetingButtonId.StartNow,
            HandleMeetingNowInteractionAsync);
        interactions.OnButton(
            MeetingButtonId.Schedule,
            HandleMeetingScheduleFormAsync);
        interactions.OnButton(
            MeetingButtonId.ScheduleSubmit,
            HandleMeetingScheduleSubmitAsync);
        interactions.OnButton(
            MeetingButtonId.ScheduleCancel,
            HandleMeetingScheduleCancelAsync);
        interactions.OnButton(
            MeetingButtonId.Help,
            ctx => HandleHelpPageAsync(ctx, "meeting"));
        interactions.OnButton(
            MeetingButtonId.CancelPrefix + "*",
            HandleMeetingCancelInteractionAsync);
        interactions.OnButton(
            MonzeButtonId.WelcomeHelp,
            HandleWelcomeSettingsAsync);
        interactions.OnButton(
                MonzeButtonId.WelcomeSettings,
                HandleWelcomeSettingsAsync);
        interactions.OnButton(
                MonzeButtonId.WelcomeGeneral,
                ctx => HandleWelcomeSectionAsync(ctx, WelcomeSetupSection.General));
        interactions.OnButton(
                MonzeButtonId.WelcomeImages,
                ctx => HandleWelcomeSectionAsync(ctx, WelcomeSetupSection.Images));
        interactions.OnButton(
                MonzeButtonId.WelcomeAuthorSection,
                ctx => HandleWelcomeSectionAsync(ctx, WelcomeSetupSection.Author));
        interactions.OnButton(
                MonzeButtonId.WelcomeAdvanced,
                ctx => HandleWelcomeSectionAsync(ctx, WelcomeSetupSection.Advanced));
        interactions.OnButton(
                MonzeButtonId.WelcomePreview,
                HandleWelcomePreviewAsync);
        interactions.OnButton(
                MonzeButtonId.WelcomeSave,
                HandleWelcomeSaveAsync);
        interactions.OnButton(
                MonzeButtonId.WelcomeSavePrefix + "*",
                HandleWelcomeSaveAsync);
        interactions.OnButton(
                MonzeButtonId.WelcomeCancel,
                HandleWelcomeCancelAsync);
        client.UseInteractions(interactions);

        client.AgentSessionStarted += evt => EnqueueAgentAsync(evt, AgentEventKind.Started);
        client.AgentSessionEnded += evt => EnqueueAgentAsync(evt, AgentEventKind.Ended);
        client.AgentSessionSummaryDone += evt => EnqueueAgentAsync(evt, AgentEventKind.SummaryDone);
        client.ChannelMessageReceived += EnqueueMessageAsync;
        client.ChannelCreated += OnChannelCreatedAsync;
        client.ChannelUpdated += OnChannelUpdatedAsync;
        client.ChannelDeleted += OnChannelDeletedAsync;
        client.ClanUserAdded += EnqueueWelcomeAsync;
        client.VoiceJoined += OnVoiceJoinedAsync;
        client.VoiceLeaved += OnVoiceLeavedAsync;
        client.VoiceEnded += OnVoiceEndedAsync;

        using var runtimeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var runtimeToken = runtimeCts.Token;
        _runtimeToken = runtimeToken;
        client.Connected += () => OnClientConnectedAsync(client);
        client.Disconnected += OnClientDisconnectedAsync;
        client.Reconnecting += OnClientReconnectingAsync;
        var messageWorker = ConsumeMessagesAsync(runtimeToken);
        var agentWorker = ConsumeAgentEventsAsync(client, runtimeToken);
        var welcomeWorker = ConsumeWelcomeAsync(client, runtimeToken);
        var roleWorker = Task.CompletedTask;
        var outboxWorker = Task.CompletedTask;

        try
        {
            await LoginWithRetryAsync(client, runtimeToken);
            await RunStartupOperationWithRetryAsync(
                () => RefreshClansAsync(client, runtimeToken),
                "clan discovery",
                runtimeToken);
            if (!string.IsNullOrWhiteSpace(options.AgentEventUrl))
            {
                await client.ConnectAgentSseAsync(runtimeToken);
            }

            outboxWorker = RunOutboxWorkerAsync(client, runtimeToken);
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

                await Task.Delay(TimeSpan.FromSeconds(1), runtimeToken);
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
            runtimeCts.Cancel();
            _messageIngress.Complete();
            _agentIngress.Writer.TryComplete();
            _welcomeIngress.Writer.TryComplete();
            try
            {
                await Task.WhenAll(messageWorker, agentWorker, welcomeWorker, roleWorker, outboxWorker);
            }
            catch (OperationCanceledException) when (runtimeToken.IsCancellationRequested)
            {
            }
        }
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

