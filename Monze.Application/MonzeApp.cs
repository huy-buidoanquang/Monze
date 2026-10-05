using Monze.Application.Commands;

namespace Monze.Application;

public sealed partial class MonzeApp
{
    private readonly IAuthorizationRepository _authorization;
    private readonly IWelcomeRepository _welcome;
    private readonly IRoleRepository _roles;
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
        IWelcomeRepository welcome,
        IRoleRepository roles,
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
        _welcome = welcome;
        _roles = roles;
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
