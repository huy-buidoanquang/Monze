using Monze.Application;
using Monze.Domain;
using Monze.Infrastructure.Caching;

namespace Monze.Tests;

internal sealed class MonzeAppTestDependencies :
    IAuthorizationRepository,
    IWelcomeRepository,
    IRoleRepository,
    IMeetingRepository,
    ISchedulingRepository,
    IAiUsageRepository,
    IMessageHistoryRepository,
    IMezonRoleGateway,
    IAiProvider
{
    public bool IsOwner { get; set; }
    public bool IsAdmin { get; set; }
    public bool DelegateChanged { get; set; } = true;
    public (long ClanId, long ActorUserId, long UserId, bool Enabled)? DelegateMutation { get; set; }

    public long WelcomeVersion { get; set; } = 1;
    public WelcomeSettings? WelcomeSettings { get; set; }
    public bool? WelcomeEnabled { get; private set; }
    public string? WelcomeText { get; private set; }
    public WelcomeEmbedSettings? WelcomeEmbed { get; private set; }
    public int WelcomeConfigurationWrites { get; private set; }

    public bool RoleAutomationEnabled { get; set; }
    public bool RoleMutationSucceeded { get; set; } = true;
    public IReadOnlyList<AutoRoleRule> RoleRules { get; set; } = [];
    public List<(long ClanId, long RoleId, long UserId)> RecordedRoleGrants { get; } = [];
    public (long ClanId, long RoleId, RoleRuleKind Kind, string? Condition)? SavedRoleRule { get; private set; }
    public (long ClanId, long RoleId, RoleRuleKind Kind)? RemovedRoleRule { get; private set; }

    public long MeetingSessionId { get; set; } = 42;
    public bool MeetingSuggestionSucceeded { get; set; } = true;
    public int MeetingCreateCalls { get; private set; }
    public (long SessionId, long VoiceChannelId, string? Label)? MeetingSuggestion { get; private set; }
    public (long SessionId, long ChannelId, long MessageId)? MeetingInvitationBinding { get; private set; }
    public MeetingSummaryRecord? MeetingSummary { get; set; }
    public Queue<PendingMeetingSummary> PendingMeetingSummaries { get; } = new();
    public List<int> PendingSummaryClaimLimits { get; } = [];
    public List<(string RoomId, string Error, string? LeaseToken)> SummaryRetries { get; } = [];

    public long ScheduleId { get; set; } = 7;
    public bool ScheduleCancelled { get; set; } = true;
    public IReadOnlyList<MeetingScheduleSummary> MeetingSchedules { get; set; } = [];
    public int ScheduleCreateCalls { get; private set; }

    public bool AiAllowed { get; set; } = true;
    public bool HistoryHasGap { get; set; }
    public int AiBudgetCalls { get; private set; }
    public List<(long ClanId, long UserId, int Tokens)> AiRefunds { get; } = [];
    public int AiProviderCalls { get; private set; }
    public string? AiResponse { get; set; } = "Kết quả AI";
    public Func<string, string, CancellationToken, Task<string?>>? AiHandler { get; set; }
    public string? LastAiInstruction { get; private set; }
    public string? LastAiInput { get; private set; }

    public IReadOnlyList<MemberRoleSnapshot> Members { get; set; } = [];
    public RoleResolutionResult RoleResolution { get; set; } = new(false, 0, string.Empty);
    public RoleAssignmentResult RoleAssignment { get; set; } = new(true, 0);
    public List<(long ClanId, long RoleId, long UserId)> RoleAssignments { get; } = [];

    public MonzeApp CreateApp(
        bool withAi = false,
        bool withRoleGateway = false,
        AiExecutionOptions? aiOptions = null,
        TimeProvider? timeProvider = null)
    {
        var app = new MonzeApp(
            this,
            this,
            this,
            this,
            this,
            this,
            this,
            new MemoryWelcomeDraftStore(),
            withAi ? this : null,
            aiOptions: aiOptions,
            timeProvider: timeProvider);
        if (withRoleGateway)
        {
            app.AttachRoleGateway(this);
        }

        return app;
    }

    public Task<bool> IsOwnerAsync(long clanId, long userId, CancellationToken cancellationToken)
        => Task.FromResult(IsOwner);

    public Task<bool> IsAdminAsync(long clanId, long userId, CancellationToken cancellationToken)
        => Task.FromResult(IsAdmin || IsOwner);

    public Task<bool> SetDelegateAsync(
        long clanId,
        long actorUserId,
        long userId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        DelegateMutation = (clanId, actorUserId, userId, enabled);
        return Task.FromResult(DelegateChanged);
    }

    public Task<long> SetWelcomeAsync(
        long clanId,
        long actorUserId,
        bool enabled,
        string? text,
        CancellationToken cancellationToken)
    {
        WelcomeEnabled = enabled;
        WelcomeText = text;
        return Task.FromResult(WelcomeVersion);
    }

    public Task<long> SetWelcomeMessageAsync(
        long clanId,
        long actorUserId,
        string text,
        CancellationToken cancellationToken)
    {
        WelcomeText = text;
        return Task.FromResult(WelcomeVersion);
    }

    public Task<long> RemoveWelcomeMessageAsync(
        long clanId,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        WelcomeText = null;
        return Task.FromResult(WelcomeVersion);
    }

    public Task<long> SetWelcomeConfigurationAsync(
        long clanId,
        long actorUserId,
        bool enabled,
        string? text,
        WelcomeEmbedSettings embed,
        CancellationToken cancellationToken,
        long? expectedVersion = null)
    {
        if (expectedVersion is not null && expectedVersion != (WelcomeSettings?.Version ?? 0))
        {
            return Task.FromResult(0L);
        }
        WelcomeConfigurationWrites++;
        WelcomeEnabled = enabled;
        WelcomeText = text;
        WelcomeEmbed = embed;
        return Task.FromResult(WelcomeVersion);
    }

    public Task<long> RemoveWelcomeEmbedAsync(
        long clanId,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        WelcomeEmbed = null;
        return Task.FromResult(WelcomeVersion);
    }

    public Task<WelcomeSettings?> GetWelcomeAsync(long clanId, CancellationToken cancellationToken)
        => Task.FromResult(WelcomeSettings);

    public Task<bool> TryClaimWelcomeAsync(long clanId, long userId, CancellationToken cancellationToken)
        => Task.FromResult(true);

    public Task ReleaseWelcomeClaimAsync(long clanId, long userId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<bool> IsRoleAutomationEnabledAsync(long clanId, CancellationToken cancellationToken)
        => Task.FromResult(RoleAutomationEnabled);

    public Task<bool> SetRoleAutomationEnabledAsync(
        long clanId,
        long actorUserId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        RoleAutomationEnabled = enabled;
        return Task.FromResult(RoleMutationSucceeded);
    }

    public Task<IReadOnlyList<AutoRoleRule>> ListEnabledRoleRulesAsync(
        long clanId,
        CancellationToken cancellationToken)
        => Task.FromResult(RoleRules.Where(rule => rule.ClanId == clanId).ToArray() as IReadOnlyList<AutoRoleRule>);

    public Task<IReadOnlyList<AutoRoleRule>> ListEnabledRoleRulesAsync(CancellationToken cancellationToken)
        => Task.FromResult(RoleRules);

    public Task<bool> SetRoleRuleAsync(
        long clanId,
        long actorUserId,
        long roleId,
        RoleRuleKind kind,
        string? conditionValue,
        CancellationToken cancellationToken)
    {
        SavedRoleRule = (clanId, roleId, kind, conditionValue);
        return Task.FromResult(RoleMutationSucceeded);
    }

    public Task<bool> RemoveRoleRuleAsync(
        long clanId,
        long actorUserId,
        long roleId,
        RoleRuleKind kind,
        CancellationToken cancellationToken)
    {
        RemovedRoleRule = (clanId, roleId, kind);
        return Task.FromResult(RoleMutationSucceeded);
    }

    public Task RecordRoleGrantAsync(
        long clanId,
        long roleId,
        long userId,
        CancellationToken cancellationToken)
    {
        RecordedRoleGrants.Add((clanId, roleId, userId));
        return Task.CompletedTask;
    }

    public Task<long> CreateMeetingAsync(
        long clanId,
        long channelId,
        long userId,
        long? eventId,
        CancellationToken cancellationToken)
    {
        MeetingCreateCalls++;
        return Task.FromResult(MeetingSessionId);
    }

    public Task<IReadOnlySet<long>> ActiveVoiceClaimsAsync(long clanId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlySet<long>>(new HashSet<long>());

    public Task<bool> SuggestMeetingAsync(
        long sessionId,
        long voiceChannelId,
        DateTimeOffset claimUntil,
        CancellationToken cancellationToken,
        string? voiceChannelLabel = null,
        string? meetingTitle = null)
    {
        MeetingSuggestion = (sessionId, voiceChannelId, voiceChannelLabel);
        return Task.FromResult(MeetingSuggestionSucceeded);
    }

    public Task<MeetingSessionBinding?> BindAgentSessionAsync(
        long clanId,
        long voiceChannelId,
        string roomId,
        long requesterId,
        CancellationToken cancellationToken,
        string? voiceChannelLabel = null)
        => Task.FromResult<MeetingSessionBinding?>(null);

    public Task<bool> TryClaimInboxAsync(string source, string eventKey, CancellationToken cancellationToken)
        => Task.FromResult(true);

    public Task ReleaseInboxAsync(string source, string eventKey, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task PurgeInboxAsync(DateTimeOffset before, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<MeetingSessionBinding?> MarkMeetingEndedAsync(string roomId, CancellationToken cancellationToken)
        => Task.FromResult<MeetingSessionBinding?>(null);

    public Task<MeetingSummaryContext?> GetSummaryContextAsync(string roomId, CancellationToken cancellationToken)
        => Task.FromResult<MeetingSummaryContext?>(null);

    public Task<bool> StoreSummaryAsync(
        string roomId,
        string summary,
        string? transcript,
        CancellationToken cancellationToken,
        string? leaseToken = null)
        => Task.FromResult(true);

    public Task<bool> StoreSummaryAsync(
        string roomId,
        string summary,
        string? transcript,
        MeetingSummaryDelivery? delivery,
        CancellationToken cancellationToken,
        string? leaseToken = null)
        => Task.FromResult(true);

    public Task MarkSummaryPendingAsync(string roomId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<IReadOnlyList<PendingMeetingSummary>> ListPendingSummariesAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        lock (PendingMeetingSummaries)
        {
            PendingSummaryClaimLimits.Add(limit);
            var rows = new List<PendingMeetingSummary>(Math.Min(limit, PendingMeetingSummaries.Count));
            while (rows.Count < limit && PendingMeetingSummaries.TryDequeue(out var item))
            {
                rows.Add(item);
            }

            return Task.FromResult<IReadOnlyList<PendingMeetingSummary>>(rows);
        }
    }

    public Task RecordSummaryRetryAsync(
        string roomId,
        string error,
        CancellationToken cancellationToken,
        string? leaseToken = null)
    {
        lock (SummaryRetries)
        {
            SummaryRetries.Add((roomId, error, leaseToken));
        }

        return Task.CompletedTask;
    }

    public Task<MeetingSummaryRecord?> GetSummaryAsync(
        long clanId,
        long sessionId,
        CancellationToken cancellationToken)
        => Task.FromResult(MeetingSummary is { ClanId: var summaryClanId, SessionId: var summarySessionId }
            && summaryClanId == clanId
            && summarySessionId == sessionId
                ? MeetingSummary
                : null);

    public Task SetSessionInvitationMessageAsync(
        long sessionId,
        long channelId,
        long messageId,
        CancellationToken cancellationToken)
    {
        MeetingInvitationBinding = (sessionId, channelId, messageId);
        return Task.CompletedTask;
    }

    public Task SetSessionStatusMessageAsync(
        long sessionId,
        long channelId,
        long messageId,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task CloseMeetingContextAsync(long clanId, long voiceChannelId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task CloseStartedMeetingContextsAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task ExpireSuggestedAsync(DateTimeOffset now, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<long> CreateMeetingScheduleAsync(
        long clanId,
        long channelId,
        long userId,
        string name,
        MeetingScheduleKind kind,
        string whenText,
        string timeZoneId,
        DateTimeOffset nextRunAt,
        CancellationToken cancellationToken)
    {
        ScheduleCreateCalls++;
        return Task.FromResult(ScheduleId);
    }

    public Task<IReadOnlyList<MeetingScheduleSummary>> ListMeetingSchedulesAsync(
        long clanId,
        long channelId,
        long userId,
        int limit,
        CancellationToken cancellationToken)
        => Task.FromResult(MeetingSchedules);

    public Task<bool> CancelMeetingScheduleAsync(
        long clanId,
        long channelId,
        long userId,
        long scheduleId,
        CancellationToken cancellationToken)
        => Task.FromResult(ScheduleCancelled);

    public Task<IReadOnlyList<DueMeetingSchedule>> ClaimDueMeetingSchedulesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DueMeetingSchedule>>([]);

    public Task CompleteMeetingScheduleAsync(
        long id,
        string leaseToken,
        DateTimeOffset? nextRunAt,
        bool failed,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<(bool Allowed, int Used)> ConsumeAiAsync(
        long clanId,
        long userId,
        int tokens,
        int dailyCap,
        CancellationToken cancellationToken)
    {
        AiBudgetCalls++;
        return Task.FromResult((AiAllowed, AiAllowed ? tokens : dailyCap));
    }

    public Task RefundAiAsync(long clanId, long userId, int tokens, CancellationToken cancellationToken)
    {
        AiRefunds.Add((clanId, userId, tokens));
        return Task.CompletedTask;
    }

    public Task<bool> ChannelPersistsAsync(long clanId, long channelId, CancellationToken cancellationToken)
        => Task.FromResult(true);

    public Task MarkChannelGapAsync(
        long clanId,
        long channelId,
        long messageId,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<bool> ChannelHasGapAsync(long clanId, long channelId, CancellationToken cancellationToken)
        => Task.FromResult(HistoryHasGap);

    public Task<IReadOnlyList<MemberRoleSnapshot>> ListMembersAsync(
        long clanId,
        CancellationToken cancellationToken)
        => Task.FromResult(Members);

    public Task<bool> IsMemberAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < Members.Count; i++)
        {
            if (Members[i].UserId == userId)
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task<RoleResolutionResult> ResolveRoleAsync(
        long clanId,
        string roleSelector,
        CancellationToken cancellationToken)
        => Task.FromResult(RoleResolution);

    public Task<RoleAssignmentResult> AddUserToRoleAsync(
        long clanId,
        long roleId,
        long userId,
        CancellationToken cancellationToken)
    {
        RoleAssignments.Add((clanId, roleId, userId));
        return Task.FromResult(RoleAssignment with { RoleId = roleId });
    }

    public Task<string?> CompleteAsync(string instruction, string input, CancellationToken cancellationToken)
    {
        AiProviderCalls++;
        LastAiInstruction = instruction;
        LastAiInput = input;
        return AiHandler is null
            ? Task.FromResult(AiResponse)
            : AiHandler(instruction, input, cancellationToken);
    }
}
