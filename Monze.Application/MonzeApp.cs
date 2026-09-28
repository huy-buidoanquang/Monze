using Monze.Application.Commands;
using Monze.Domain;

namespace Monze.Application;

public sealed partial class MonzeApp
{
    private readonly IAuthorizationRepository _authorization;
    private readonly ICommunityRepository _community;
    private readonly IMeetingRepository _meeting;
    private readonly ISchedulingRepository _scheduling;
    private readonly IOutboxRepository _outbox;
    private readonly IAiUsageRepository _aiUsage;
    private readonly IMessageHistoryRepository _messageHistory;
    private readonly IAiProvider? _ai;
    private readonly IReadModelCache _readModelCache;
    private readonly MonzeCommandOptions _commandOptions;
    private readonly IWelcomeDraftStore _welcomeDrafts;
    private readonly AiExecutionOptions _aiOptions;
    private readonly SemaphoreSlim _aiConcurrency;
    private IMezonRoleGateway? _roleGateway;

    public MonzeApp(
        IAuthorizationRepository authorization,
        ICommunityRepository community,
        IMeetingRepository meeting,
        ISchedulingRepository scheduling,
        IOutboxRepository outbox,
        IAiUsageRepository aiUsage,
        IMessageHistoryRepository messageHistory,
        IWelcomeDraftStore welcomeDrafts,
        IAiProvider? ai = null,
        IReadModelCache? readModelCache = null,
        MonzeCommandOptions? commandOptions = null,
        AiExecutionOptions? aiOptions = null)
    {
        _authorization = authorization;
        _community = community;
        _meeting = meeting;
        _scheduling = scheduling;
        _outbox = outbox;
        _aiUsage = aiUsage;
        _messageHistory = messageHistory;
        _ai = ai;
        _readModelCache = readModelCache ?? new DisabledReadModelCache();
        _commandOptions = commandOptions ?? MonzeCommandOptions.Default;
        _welcomeDrafts = welcomeDrafts;
        _aiOptions = (aiOptions ?? AiExecutionOptions.Default).Normalize();
        _aiConcurrency = new SemaphoreSlim(_aiOptions.MaxConcurrentRequests, _aiOptions.MaxConcurrentRequests);
    }

    public void AttachRoleGateway(IMezonRoleGateway roleGateway)
        => _roleGateway = roleGateway;

    /// <summary>
    /// Compatibility entry point for callers that already expose command
    /// arguments as an interface. The bot ingress path uses the typed view
    /// overload below to avoid boxing the command arguments struct.
    /// </summary>
    public Task<CommandOutcome> HandleMonzeAsync(
        long clanId,
        long channelId,
        long userId,
        long messageId,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        Func<long, CancellationToken, Task<string>>? resolveUserLabel = null,
        long? mentionedUserId = null)
        => HandleMonzeAsync(
            clanId,
            channelId,
            userId,
            messageId,
            new CommandArguments(args),
            cancellationToken,
            resolveUserLabel,
            mentionedUserId);

    public async Task<CommandOutcome> HandleMonzeAsync(
        long clanId,
        long channelId,
        long userId,
        long messageId,
        CommandArguments args,
        CancellationToken cancellationToken,
        Func<long, CancellationToken, Task<string>>? resolveUserLabel = null,
        long? mentionedUserId = null)
    {
        if (args.Count == 0)
        {
            return await HelpOutcomeAsync(clanId, userId, string.Empty, cancellationToken);
        }

        if (args[0].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase))
        {
            var topic = args.Count > 1 ? args[1] : string.Empty;
            return await HelpOutcomeAsync(clanId, userId, topic, cancellationToken);
        }

        var module = MonzeCommandNames.Normalize(args[0]);
        var rest = args.Slice(1);
        if (rest.Length > 0 && rest[0].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase))
        {
            return await HelpOutcomeAsync(clanId, userId, module, cancellationToken);
        }

        return module switch
        {
            MonzeCommandNames.Setup => await SetupAsync(clanId, channelId, userId, rest, mentionedUserId, cancellationToken),
            MonzeCommandNames.Welcome => await WelcomeAsync(clanId, channelId, userId, rest, cancellationToken),
            MonzeCommandNames.Event => await EventAsync(clanId, channelId, userId, rest, cancellationToken),
            MonzeCommandNames.Announce => await AdminTextAsync(clanId, userId, channelId, OutboxKind.Announcement, "announce:" + messageId, rest, cancellationToken),
            MonzeCommandNames.Outbox => await OutboxAsync(clanId, userId, rest, cancellationToken),
            MonzeCommandNames.Faq => await FaqAsync(clanId, userId, rest, cancellationToken),
            MonzeCommandNames.Info => await InfoAsync(clanId, rest, cancellationToken),
            MonzeCommandNames.Points => await PointsAsync(clanId, userId, rest, cancellationToken),
            MonzeCommandNames.Leaderboard => await LeaderboardAsync(clanId, resolveUserLabel, cancellationToken),
            MonzeCommandNames.Spin => await SpinAsync(clanId, userId, cancellationToken),
            MonzeCommandNames.Topic => await TopicAsync(clanId, userId, rest, cancellationToken),
            MonzeCommandNames.Summarize or MonzeCommandNames.Translate or MonzeCommandNames.Rewrite or MonzeCommandNames.Shorten => await AiAsync(clanId, channelId, userId, module, rest, cancellationToken),
            MonzeCommandNames.Role => await RoleAsync(clanId, userId, rest, cancellationToken),
            _ => Say(MonzeMessages.UnknownCommand(_commandOptions))
        };
    }

    public async Task<CommandOutcome> HandleMeetingAsync(
        long clanId,
        long channelId,
        long userId,
        IReadOnlyList<string> args,
        Func<CancellationToken, Task<MeetingVoiceCandidate?>> pickVoice,
        CancellationToken cancellationToken)
    {
        if (!MeetingCommandParser.TryParse(args, out var request) || request is null)
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Meeting, cancellationToken);
        }

        if (request.Kind != MeetingScheduleKind.Now)
        {
            const string timeZoneId = "Asia/Ho_Chi_Minh";
            if (!MeetingScheduleCalculator.TryGetNext(
                    request.Kind,
                    request.WhenText!,
                    timeZoneId,
                    DateTimeOffset.UtcNow,
                    out var next,
                    out var error))
            {
                return Say(error ?? MonzeMessages.InvalidTime);
            }

            await _scheduling.CreateMeetingScheduleAsync(
                clanId,
                channelId,
                userId,
                request.Kind,
                request.WhenText!,
                timeZoneId,
                next,
                cancellationToken);
            return Say(MonzeMessages.ScheduleSaved(request.Kind, next));
        }

        var voice = await pickVoice(cancellationToken);
        if (voice is null)
        {
            return Say(MonzeMessages.NoVoiceRoom);
        }

        var sessionId = await _meeting.CreateMeetingAsync(clanId, channelId, userId, null, cancellationToken);
        if (!await _meeting.SuggestMeetingAsync(sessionId, voice.VoiceChannelId, DateTimeOffset.UtcNow.AddMinutes(20), cancellationToken))
        {
            return Say(MonzeMessages.VoiceClaimConflict, tone: MonzeTone.Warn);
        }

        return Say(MonzeMessages.MeetingSuggested(voice.Label));
    }

    public async Task<CommandOutcome> HandleSummaryAsync(
        long clanId,
        long voiceChannelId,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (clanId <= 0)
        {
            return Say(MonzeMessages.UnknownClan, title: MonzeMessages.TitleSummary, tone: MonzeTone.Error);
        }

        if (args.Count > 0 && args[0].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase))
        {
            return await HelpOutcomeAsync(clanId, 0, MonzeCommandNames.Summary, cancellationToken);
        }

        var text = await _meeting.LatestPostedSummaryAsync(clanId, voiceChannelId, cancellationToken);
        return Say(text ?? MonzeMessages.NoSummary);
    }

    public async Task<CommandOutcome> SetWelcomeAsync(
        long clanId,
        long userId,
        bool enabled,
        string? text,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.AdminOnly, tone: MonzeTone.Error);
        }

        var version = await _authorization.SetWelcomeAsync(clanId, enabled, text, cancellationToken);
        try
        {
            await _readModelCache.InvalidateAsync(clanId, "welcome", "settings", version, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Cache invalidation is best effort; PostgreSQL remains authoritative.
        }

        return new CommandOutcome
        {
            Title = MonzeMessages.TitleWelcome,
            Text = enabled ? MonzeMessages.WelcomeEnabled : MonzeMessages.WelcomeDisabled,
            ShowWelcomeHelp = true
        };
    }

    private static CommandOutcome Say(string text, string title = MonzeMessages.TitleMonze, MonzeTone tone = MonzeTone.Info)
        => new() { Title = title, Text = text, Tone = tone };

    private async Task<CommandOutcome> InfoAsync(
        long clanId,
        CommandArguments rest,
        CancellationToken cancellationToken)
    {
        var query = rest.Join(' ').Trim();
        if (query.Length == 0)
        {
            return await HelpOutcomeAsync(clanId, 0, MonzeCommandNames.Info, cancellationToken);
        }

        return Say(await _community.FindFaqAsync(clanId, query, cancellationToken) ?? MonzeMessages.NoFaq);
    }

    private async Task<CommandOutcome> HelpOutcomeAsync(
        long clanId,
        long userId,
        string topic,
        CancellationToken cancellationToken)
    {
        var isAdmin = await _authorization.IsAdminAsync(clanId, userId, cancellationToken);
        var normalizedTopic = string.IsNullOrWhiteSpace(topic)
            ? string.Empty
            : MonzeCommandNames.Normalize(topic);
        return new CommandOutcome
        {
            Title = MonzeMessages.TitleHelp,
            Text = MonzeMessages.CommandHelp(
                normalizedTopic.Length == 0 ? MonzeCommandNames.Monze : normalizedTopic,
                _commandOptions,
                isAdmin),
            HelpTopic = normalizedTopic,
            HelpForAdmin = isAdmin,
            ShowHelpButtons = true
        };
    }
}
