using Monze.Application.Commands;
using Monze.Domain;

namespace Monze.Application;

public sealed partial class MonzeApp
{
    private readonly IAuthorizationRepository _authorization;
    private readonly IMeetingRepository _meeting;
    private readonly ISchedulingRepository _scheduling;
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
        IMeetingRepository meeting,
        ISchedulingRepository scheduling,
        IAiUsageRepository aiUsage,
        IMessageHistoryRepository messageHistory,
        IWelcomeDraftStore welcomeDrafts,
        IAiProvider? ai = null,
        IReadModelCache? readModelCache = null,
        MonzeCommandOptions? commandOptions = null,
        AiExecutionOptions? aiOptions = null)
    {
        _authorization = authorization;
        _meeting = meeting;
        _scheduling = scheduling;
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
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        long? mentionedUserId = null,
        AiRequestContext? aiRequest = null)
        => HandleMonzeAsync(
            clanId,
            channelId,
            userId,
            new CommandArguments(args),
            cancellationToken,
            mentionedUserId,
            aiRequest);

    public async Task<CommandOutcome> HandleMonzeAsync(
        long clanId,
        long channelId,
        long userId,
        CommandArguments args,
        CancellationToken cancellationToken,
        long? mentionedUserId = null,
        AiRequestContext? aiRequest = null)
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
            MonzeCommandNames.Ai => await AiAsync(clanId, channelId, userId, rest, aiRequest, cancellationToken),
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
        if (args.Count == 0)
        {
            var schedules = await _scheduling.ListMeetingSchedulesAsync(
                clanId,
                channelId,
                userId,
                20,
                cancellationToken);
            return new CommandOutcome
            {
                Title = MonzeMessages.TitleMeeting,
                Text = schedules.Count == 0 ? MonzeMessages.MeetingScheduleEmpty : string.Empty,
                ShowMeetingSchedules = true,
                MeetingSchedules = schedules
            };
        }

        if (!MeetingCommandParser.TryParse(args, out var request) || request is null)
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Meeting, cancellationToken);
        }

        if (request.IsCancel)
        {
            var cancelled = await _scheduling.CancelMeetingScheduleAsync(
                clanId,
                channelId,
                userId,
                request.CancelScheduleId!.Value,
                cancellationToken);
            return Say(cancelled ? MonzeMessages.MeetingScheduleCancelled : MonzeMessages.MeetingScheduleNotFound,
                title: MonzeMessages.TitleMeeting,
                tone: cancelled ? MonzeTone.Ok : MonzeTone.Warn);
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

            var scheduleId = await _scheduling.CreateMeetingScheduleAsync(
                clanId,
                channelId,
                userId,
                request.Name ?? "Cuộc họp",
                request.Kind,
                request.WhenText!,
                timeZoneId,
                next,
                cancellationToken);
            return Say(MonzeMessages.ScheduleSaved(
                request.Name ?? "Cuộc họp",
                scheduleId,
                request.Kind,
                next),
                title: MonzeMessages.TitleMeeting,
                tone: MonzeTone.Ok);
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

        return new CommandOutcome
        {
            Title = MonzeMessages.TitleMeeting,
            Text = MonzeMessages.MeetingAgentInstruction,
            Tone = MonzeTone.Ok,
            MeetingInvitation = new MeetingInvitation(voice.VoiceChannelId, voice.Label, SessionId: sessionId)
        };
    }

    public async Task<CommandOutcome> HandleSummaryAsync(
        long clanId,
        long voiceChannelId,
        long userId,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (clanId <= 0)
        {
            return Say(MonzeMessages.UnknownClan, title: MonzeMessages.TitleSummary, tone: MonzeTone.Error);
        }

        if (args.Count > 0 && args[0].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase))
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Summary, cancellationToken);
        }

        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.SummaryAdminOnly, title: MonzeMessages.TitleSummary, tone: MonzeTone.Error);
        }

        if (args.Count != 1
            || !long.TryParse(args[0], out var sessionId)
            || sessionId <= 0)
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Summary, cancellationToken);
        }

        var summary = await _meeting.GetSummaryAsync(clanId, sessionId, cancellationToken);
        if (summary is null)
        {
            return Say(MonzeMessages.NoSummary, title: MonzeMessages.TitleSummary, tone: MonzeTone.Warn);
        }

        var fields = new List<CommandField>(2)
        {
            new("Tóm tắt", summary.Summary)
        };
        if (summary.StartedAt is { } started && summary.EndedAt is { } ended)
        {
            fields.Insert(0, new("Thời lượng", FormatDuration(ended - started)));
        }

        return new CommandOutcome
        {
            Title = MonzeMessages.TitleSummary,
            Text = string.Empty,
            Fields = fields
        };
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} giờ {duration.Minutes} phút"
            : duration.TotalMinutes >= 1
                ? $"{(int)duration.TotalMinutes} phút"
                : $"{Math.Max(1, duration.Seconds)} giây";

    public async Task<CommandOutcome> SetWelcomeAsync(
        long clanId,
        long userId,
        bool enabled,
        string? text,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.WelcomeAdminOnly, tone: MonzeTone.Error);
        }

        var version = await _authorization.SetWelcomeAsync(clanId, userId, enabled, text, cancellationToken);
        if (version == 0)
        {
            return Say(MonzeMessages.WelcomeAdminOnly, tone: MonzeTone.Error);
        }
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

    private async Task<CommandOutcome> HelpOutcomeAsync(
        long clanId,
        long userId,
        string topic,
        CancellationToken cancellationToken)
    {
        var isAdmin = await _authorization.IsAdminAsync(clanId, userId, cancellationToken);
        var isOwner = await _authorization.IsOwnerAsync(clanId, userId, cancellationToken);
        var normalizedTopic = string.IsNullOrWhiteSpace(topic)
            ? string.Empty
            : MonzeCommandNames.Normalize(topic);
        return new CommandOutcome
        {
            Title = MonzeMessages.TitleHelp,
            // Help is rendered from HelpTopic by MonzeMessageBuilder. Keep
            // Text empty so the legacy concatenated help string is not built
            // on every request and cannot leak back into a single field.
            Text = string.Empty,
            HelpTopic = normalizedTopic,
            HelpForAdmin = isAdmin,
            HelpForOwner = isOwner,
            CanManageWelcome = isAdmin,
            ShowHelpButtons = true
        };
    }
}
