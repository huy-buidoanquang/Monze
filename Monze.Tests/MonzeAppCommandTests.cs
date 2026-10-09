using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;
using Monze.Testing;
using Xunit;

namespace Monze.Tests;

public sealed class MonzeAppCommandTests
{
    private const long ClanId = 10;
    private const long ChannelId = 20;
    private const long UserId = 30;

    [Fact]
    public async Task Setup_requires_the_owner_and_rejects_self_delegation()
    {
        var dependencies = new MonzeAppTestDependencies
        {
            IsAdmin = true,
            Members = [new MemberRoleSnapshot(40, false, null, new HashSet<long>())]
        };
        var app = dependencies.CreateApp(withRoleGateway: true);

        var denied = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["setup", "admin", "add", "40"],
            CancellationToken.None);
        Assert.Equal(MonzeMessages.OwnerOnly, denied.Text);
        Assert.Null(dependencies.DelegateMutation);

        dependencies.IsOwner = true;
        var self = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["setup", "admin", "add", UserId.ToString()],
            CancellationToken.None);
        Assert.Equal(MonzeMessages.OwnerAlreadyAdmin, self.Text);
        Assert.Null(dependencies.DelegateMutation);

        var added = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["setup", "admin", "add", "40"],
            CancellationToken.None);
        Assert.Equal(MonzeMessages.DelegateAdded, added.Text);
        Assert.Equal((ClanId, UserId, 40L, true), dependencies.DelegateMutation);

        dependencies.DelegateMutation = null;
        var invalid = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["setup", "admin", "add", "invalid"],
            CancellationToken.None);
        Assert.Equal(MonzeCommandNames.Setup, invalid.HelpTopic);
        Assert.Null(dependencies.DelegateMutation);
    }

    [Fact]
    public async Task Setup_adds_only_current_clan_members_but_can_remove_a_former_member()
    {
        var dependencies = new MonzeAppTestDependencies { IsOwner = true };
        var app = dependencies.CreateApp(withRoleGateway: true);

        var rejected = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["setup", "admin", "add", "40"],
            CancellationToken.None);

        Assert.Equal(MonzeMessages.DelegateMustBeClanMember, rejected.Text);
        Assert.Null(dependencies.DelegateMutation);

        var removed = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["setup", "admin", "remove", "40"],
            CancellationToken.None);

        Assert.Equal(MonzeMessages.DelegateRemoved, removed.Text);
        Assert.Equal((ClanId, UserId, 40L, false), dependencies.DelegateMutation);
    }

    [Fact]
    public async Task Welcome_mutations_require_admin_and_keep_text_separate_from_embed()
    {
        var dependencies = new MonzeAppTestDependencies();
        var app = dependencies.CreateApp();

        var denied = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["welcome", "on"],
            CancellationToken.None);
        Assert.Equal(MonzeMessages.WelcomeAdminOnly, denied.Text);
        Assert.Null(dependencies.WelcomeEnabled);

        dependencies.IsAdmin = true;
        var enabled = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["welcome", "on"],
            CancellationToken.None);
        Assert.Equal(MonzeMessages.WelcomeEnabled, enabled.Text);
        Assert.True(dependencies.WelcomeEnabled);

        var message = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["welcome", "message", "Chào", "{user}"],
            CancellationToken.None);
        Assert.Equal(MonzeMessages.WelcomeMessageSaved, message.Text);
        Assert.Equal("Chào {user}", dependencies.WelcomeText);

        var removed = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["welcome", "message", "remove"],
            CancellationToken.None);
        Assert.Equal(MonzeMessages.WelcomeMessageRemoved, removed.Text);
        Assert.Null(dependencies.WelcomeText);
    }

    [Fact]
    public async Task Welcome_draft_is_scoped_normalized_and_single_use()
    {
        var dependencies = new MonzeAppTestDependencies { IsAdmin = true };
        var app = dependencies.CreateApp();
        var preview = await app.PreviewWelcomeAsync(
            ClanId,
            ChannelId,
            true,
            new WelcomeEmbedSettings(
                Title: "  Chào mừng  ",
                Url: "http://unsafe.test",
                Color: "#aabbcc"),
            CancellationToken.None,
            "Chào {user}");
        Assert.NotNull(preview.WelcomeDraftToken);
        Assert.Equal("Chào mừng", preview.WelcomeDraft?.Title);
        Assert.Null(preview.WelcomeDraft?.Url);
        Assert.Equal("#AABBCC", preview.WelcomeDraft?.Color);

        var wrongChannel = await app.SaveWelcomeDraftAsync(
            ClanId,
            ChannelId + 1,
            UserId,
            preview.WelcomeDraftToken!,
            CancellationToken.None);
        Assert.Equal(MonzeMessages.WelcomeDraftInvalid, wrongChannel.Text);

        var saved = await app.SaveWelcomeDraftAsync(
            ClanId,
            ChannelId,
            UserId,
            preview.WelcomeDraftToken!,
            CancellationToken.None);
        Assert.Equal(MonzeMessages.WelcomeEmbedSaved, saved.Text);
        Assert.Equal(1, dependencies.WelcomeConfigurationWrites);
        Assert.Equal("Chào {user}", dependencies.WelcomeText);

        var replay = await app.SaveWelcomeDraftAsync(
            ClanId,
            ChannelId,
            UserId,
            preview.WelcomeDraftToken!,
            CancellationToken.None);
        Assert.Equal(MonzeMessages.WelcomeDraftInvalid, replay.Text);
        Assert.Equal(1, dependencies.WelcomeConfigurationWrites);
    }

    [Fact]
    public async Task Welcome_draft_claim_is_released_when_the_repository_rejects_the_write()
    {
        var dependencies = new MonzeAppTestDependencies { IsAdmin = true, WelcomeVersion = 0 };
        var app = dependencies.CreateApp();
        var preview = await app.PreviewWelcomeAsync(
            ClanId,
            ChannelId,
            true,
            new WelcomeEmbedSettings(Title: "Chào mừng"),
            CancellationToken.None);

        var rejected = await app.SaveWelcomeDraftAsync(
            ClanId,
            ChannelId,
            UserId,
            preview.WelcomeDraftToken!,
            CancellationToken.None);
        Assert.Equal(MonzeTone.Error, rejected.Tone);

        dependencies.WelcomeVersion = 1;
        var retried = await app.SaveWelcomeDraftAsync(
            ClanId,
            ChannelId,
            UserId,
            preview.WelcomeDraftToken!,
            CancellationToken.None);
        Assert.Equal(MonzeMessages.WelcomeEmbedSaved, retried.Text);
    }

    [Fact]
    public async Task Role_commands_require_admin_and_store_only_supported_rules()
    {
        var dependencies = new MonzeAppTestDependencies
        {
            RoleResolution = new RoleResolutionResult(true, 77, "Member")
        };
        var app = dependencies.CreateApp(withRoleGateway: true);

        var denied = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["role", "on"],
            CancellationToken.None);
        Assert.Equal(MonzeMessages.AdminOnly, denied.Text);

        dependencies.IsAdmin = true;
        var enabled = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["role", "on"],
            CancellationToken.None);
        Assert.Equal(MonzeMessages.RoleAutomationEnabled, enabled.Text);
        Assert.True(dependencies.RoleAutomationEnabled);

        await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["role", "tenure", "Member"],
            CancellationToken.None);
        Assert.Equal((ClanId, 77L, RoleRuleKind.Tenure, "30"), dependencies.SavedRoleRule);

        await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["role", "join", "remove", "Member"],
            CancellationToken.None);
        Assert.Equal((ClanId, 77L, RoleRuleKind.OnJoin), dependencies.RemovedRoleRule);
    }

    [Fact]
    public async Task Automatic_roles_skip_bots_existing_roles_and_ineligible_tenure()
    {
        var joinedAt = DateTimeOffset.UtcNow.AddDays(-60);
        var dependencies = new MonzeAppTestDependencies
        {
            RoleAutomationEnabled = true,
            RoleRules =
            [
                new AutoRoleRule(ClanId, 70, RoleRuleKind.OnJoin, null, 1, joinedAt.AddDays(-1)),
                new AutoRoleRule(ClanId, 80, RoleRuleKind.Tenure, "30", 1)
            ],
            Members =
            [
                new MemberRoleSnapshot(1, true, joinedAt, new HashSet<long>()),
                new MemberRoleSnapshot(2, false, joinedAt, new HashSet<long> { 70 }),
                new MemberRoleSnapshot(3, false, joinedAt, new HashSet<long>()),
                new MemberRoleSnapshot(4, false, DateTimeOffset.UtcNow.AddDays(-10), new HashSet<long>())
            ]
        };
        var app = dependencies.CreateApp(withRoleGateway: true);

        await app.ApplyAutomaticRoleRulesAsync(CancellationToken.None);

        Assert.DoesNotContain(dependencies.RoleAssignments, item => item.UserId == 1);
        Assert.DoesNotContain(dependencies.RoleAssignments, item => item.UserId == 2 && item.RoleId == 70);
        Assert.Contains(dependencies.RoleAssignments, item => item.UserId == 3 && item.RoleId == 70);
        Assert.Contains(dependencies.RecordedRoleGrants, item => item.UserId == 3 && item.RoleId == 80);
        Assert.DoesNotContain(dependencies.RoleAssignments, item => item.UserId == 4 && item.RoleId == 80);
    }

    [Fact]
    [Req("REQ-TIME-001")]
    public async Task Tenure_rule_uses_the_injected_clock()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero));
        var dependencies = new MonzeAppTestDependencies
        {
            RoleAutomationEnabled = true,
            RoleRules = [new AutoRoleRule(ClanId, 80, RoleRuleKind.Tenure, "30", 1)],
            Members = [new MemberRoleSnapshot(3, false, time.GetUtcNow().AddDays(-29), new HashSet<long>())]
        };
        var app = dependencies.CreateApp(withRoleGateway: true, timeProvider: time);

        await app.ApplyAutomaticRoleRulesAsync(CancellationToken.None);
        Assert.Empty(dependencies.RoleAssignments);

        time.Advance(TimeSpan.FromDays(1));
        await app.ApplyAutomaticRoleRulesAsync(CancellationToken.None);

        Assert.Contains(dependencies.RoleAssignments, item => item.UserId == 3 && item.RoleId == 80);
    }

    [Fact]
    public async Task Automatic_roles_do_not_record_a_failed_platform_assignment()
    {
        var dependencies = new MonzeAppTestDependencies
        {
            RoleAutomationEnabled = true,
            RoleAssignment = new RoleAssignmentResult(false, 0),
            RoleRules = [new AutoRoleRule(ClanId, 70, RoleRuleKind.OnJoin, null, 1, DateTimeOffset.UtcNow.AddDays(-1))],
            Members = [new MemberRoleSnapshot(3, false, DateTimeOffset.UtcNow, new HashSet<long>())]
        };
        var app = dependencies.CreateApp(withRoleGateway: true);

        await app.ApplyAutomaticRoleRulesAsync(CancellationToken.None);

        Assert.Single(dependencies.RoleAssignments);
        Assert.Empty(dependencies.RecordedRoleGrants);
    }

    /// <summary>
    /// CAND-22 (decided: an on-join rule is for new members only): the
    /// periodic scan grants the role to a member who joined after the rule
    /// was set (a join the bot missed) but not to earlier members or to one
    /// whose join time is unknown.
    /// </summary>
    [Fact]
    [Req("REQ-ROLE-002")]
    public async Task The_periodic_scan_applies_an_on_join_rule_only_to_members_who_joined_after_it()
    {
        var ruleSet = DateTimeOffset.UtcNow.AddDays(-7);
        var dependencies = new MonzeAppTestDependencies
        {
            RoleAutomationEnabled = true,
            RoleRules = [new AutoRoleRule(ClanId, 70, RoleRuleKind.OnJoin, null, 1, ruleSet)],
            Members =
            [
                new MemberRoleSnapshot(3, false, ruleSet.AddDays(-30), new HashSet<long>()),
                new MemberRoleSnapshot(4, false, null, new HashSet<long>()),
                new MemberRoleSnapshot(5, false, ruleSet.AddDays(2), new HashSet<long>())
            ]
        };
        var app = dependencies.CreateApp(withRoleGateway: true);

        await app.ApplyAutomaticRoleRulesAsync(CancellationToken.None);

        Assert.Equal(new[] { 5L }, dependencies.RoleAssignments.Select(static item => item.UserId));
    }

    /// <summary>Regression for CAND-24: a grant that keeps failing is retried on the next scan, then less and less often.</summary>
    [Fact]
    [Req("REQ-ROLE-002")]
    public async Task Automatic_roles_back_off_a_grant_that_keeps_failing()
    {
        var dependencies = new MonzeAppTestDependencies
        {
            RoleAutomationEnabled = true,
            RoleAssignment = new RoleAssignmentResult(false, 0),
            RoleRules = [new AutoRoleRule(ClanId, 70, RoleRuleKind.OnJoin, null, 1, DateTimeOffset.UtcNow.AddDays(-1))],
            Members = [new MemberRoleSnapshot(3, false, DateTimeOffset.UtcNow, new HashSet<long>())]
        };
        var app = dependencies.CreateApp(withRoleGateway: true);
        var attemptsAfterScan = new List<int>();

        for (var scan = 0; scan < 9; scan++)
        {
            await app.ApplyAutomaticRoleRulesAsync(CancellationToken.None);
            attemptsAfterScan.Add(dependencies.RoleAssignments.Count);
        }

        // Attempts on scans 1, 2, 4 and 8: one scan skipped after the second failure, three after the third.
        Assert.Equal(new[] { 1, 2, 2, 3, 3, 3, 3, 4, 4 }, attemptsAfterScan);
        Assert.Empty(dependencies.RecordedRoleGrants);

        dependencies.RoleAssignment = new RoleAssignmentResult(true, 70);
        for (var scan = 0; scan < 8 && dependencies.RecordedRoleGrants.Count == 0; scan++)
        {
            await app.ApplyAutomaticRoleRulesAsync(CancellationToken.None);
        }

        Assert.Single(dependencies.RecordedRoleGrants);
        await app.ApplyAutomaticRoleRulesAsync(CancellationToken.None);
        Assert.Equal(2, dependencies.RecordedRoleGrants.Count);
    }

    [Fact]
    public async Task Meeting_now_does_not_create_a_session_without_an_available_room()
    {
        var dependencies = new MonzeAppTestDependencies();
        var app = dependencies.CreateApp();

        var noRoom = await app.HandleMeetingAsync(
            ClanId,
            ChannelId,
            UserId,
            ["now"],
            _ => Task.FromResult<MeetingVoiceCandidate?>(null),
            CancellationToken.None);
        Assert.Equal(MonzeMessages.NoVoiceRoom, noRoom.Text);
        Assert.Equal(0, dependencies.MeetingCreateCalls);

        var success = await app.HandleMeetingAsync(
            ClanId,
            ChannelId,
            UserId,
            ["now"],
            _ => Task.FromResult<MeetingVoiceCandidate?>(new MeetingVoiceCandidate(50, "voice-room")),
            CancellationToken.None);
        Assert.Equal(MonzeMessages.MeetingAgentInstruction, success.Text);
        Assert.Equal(42, success.MeetingInvitation?.SessionId);
        Assert.Equal((42L, 50L, "voice-room"), dependencies.MeetingSuggestion);

        dependencies.MeetingSuggestionSucceeded = false;
        var conflicted = await app.HandleMeetingAsync(
            ClanId,
            ChannelId,
            UserId,
            ["now"],
            _ => Task.FromResult<MeetingVoiceCandidate?>(new MeetingVoiceCandidate(51, "busy-room")),
            CancellationToken.None);
        Assert.Equal(MonzeMessages.VoiceClaimConflict, conflicted.Text);
        Assert.Null(conflicted.MeetingInvitation);
    }

    /// <summary>
    /// CAND-27 (decided: tell the creator): a schedule less than 30 minutes
    /// from another one the creator can see in the channel is still saved,
    /// with a warning that names the nearby schedule.
    /// </summary>
    [Fact]
    [Req("REQ-MTG-002")]
    public async Task A_schedule_near_another_is_saved_with_a_warning()
    {
        var day = DateTimeOffset.UtcNow.AddDays(2);
        var at = new DateTimeOffset(day.Year, day.Month, day.Day, 18, 30, 0, TimeSpan.FromHours(7));
        var dependencies = new MonzeAppTestDependencies
        {
            MeetingSchedules =
            [
                new MeetingScheduleSummary(11, "Standup", MeetingScheduleKind.Once, at.AddMinutes(15), "Asia/Ho_Chi_Minh", UserId),
                new MeetingScheduleSummary(12, "Retro", MeetingScheduleKind.Once, at.AddHours(2), "Asia/Ho_Chi_Minh", UserId)
            ]
        };
        var app = dependencies.CreateApp();

        var scheduled = await app.HandleMeetingAsync(
            ClanId,
            ChannelId,
            UserId,
            ["Daily", at.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture), "18:30", "once"],
            _ => Task.FromResult<MeetingVoiceCandidate?>(null),
            CancellationToken.None);

        Assert.Equal(MonzeTone.Warn, scheduled.Tone);
        Assert.Equal(1, dependencies.ScheduleCreateCalls);
        Assert.Contains("\"Standup\" (#11", scheduled.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Retro", scheduled.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Meeting_schedule_and_cancel_use_the_current_text_channel()
    {
        var dependencies = new MonzeAppTestDependencies();
        var app = dependencies.CreateApp();
        var future = DateTimeOffset.UtcNow.AddDays(2);

        var scheduled = await app.HandleMeetingAsync(
            ClanId,
            ChannelId,
            UserId,
            ["Daily", future.ToString("dd/MM/yyyy"), "18:30", "once"],
            _ => Task.FromResult<MeetingVoiceCandidate?>(null),
            CancellationToken.None);
        Assert.Equal(MonzeTone.Ok, scheduled.Tone);
        Assert.Equal(1, dependencies.ScheduleCreateCalls);

        var cancelled = await app.HandleMeetingAsync(
            ClanId,
            ChannelId,
            UserId,
            ["cancel", dependencies.ScheduleId.ToString()],
            _ => Task.FromResult<MeetingVoiceCandidate?>(null),
            CancellationToken.None);
        Assert.Equal(MonzeMessages.MeetingScheduleCancelled, cancelled.Text);
    }

    [Fact]
    public async Task Saved_schedule_reports_the_next_run_in_the_schedule_time_zone()
    {
        var dependencies = new MonzeAppTestDependencies();
        var app = dependencies.CreateApp();
        var future = DateTimeOffset.UtcNow.AddDays(2);

        var scheduled = await app.HandleMeetingAsync(
            ClanId,
            ChannelId,
            UserId,
            ["Review", future.ToString("dd/MM/yyyy"), "18:30", "once"],
            _ => Task.FromResult<MeetingVoiceCandidate?>(null),
            CancellationToken.None);

        Assert.Equal(MonzeTone.Ok, scheduled.Tone);
        Assert.Contains("18:30 (Asia/Ho_Chi_Minh)", scheduled.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("UTC", scheduled.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_is_admin_only_and_scoped_to_clan_and_session()
    {
        var dependencies = new MonzeAppTestDependencies
        {
            MeetingSummary = new MeetingSummaryRecord(
                42,
                50,
                ChannelId,
                "summary",
                null,
                null,
                null,
                ClanId: ClanId)
        };
        var app = dependencies.CreateApp();

        var denied = await app.HandleSummaryAsync(
            ClanId,
            UserId,
            ["42"],
            CancellationToken.None);
        Assert.Equal(MonzeMessages.SummaryAdminOnly, denied.Text);

        dependencies.IsAdmin = true;
        var missing = await app.HandleSummaryAsync(
            ClanId + 1,
            UserId,
            ["42"],
            CancellationToken.None);
        Assert.Equal(MonzeMessages.NoSummary, missing.Text);

        var found = await app.HandleSummaryAsync(
            ClanId,
            UserId,
            ["42"],
            CancellationToken.None);
        Assert.Same(dependencies.MeetingSummary, found.MeetingSummary);
    }

    [Fact]
    public async Task Ai_rejects_stale_reply_and_budget_denial_before_calling_provider()
    {
        var dependencies = new MonzeAppTestDependencies();
        var app = dependencies.CreateApp(withAi: true);

        var stale = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["ai", "summary"],
            CancellationToken.None,
            aiRequest: new AiRequestContext(99, "history", ReplyWithinOneHour: false));
        Assert.Equal(MonzeMessages.AiHistoryWindow, stale.Text);
        Assert.Equal(0, dependencies.AiBudgetCalls);
        Assert.Equal(0, dependencies.AiProviderCalls);

        dependencies.AiAllowed = false;
        var denied = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["ai", "translate", "hello"],
            CancellationToken.None);
        Assert.Equal(MonzeMessages.AiBudgetExceeded, denied.Text);
        Assert.Equal(1, dependencies.AiBudgetCalls);
        Assert.Equal(0, dependencies.AiProviderCalls);
    }

    /// <summary>Regression for CAND-05: a request the provider does not answer gives its tokens back.</summary>
    [Fact]
    [Req("REQ-AI-001")]
    [Covers("port:IAiUsageRepository.RefundAiAsync")]
    public async Task Ai_refunds_the_budget_when_the_provider_gives_no_answer()
    {
        var dependencies = new MonzeAppTestDependencies { AiResponse = null };
        var app = dependencies.CreateApp(withAi: true);

        var failed = await app.HandleMonzeAsync(ClanId, ChannelId, UserId, ["ai", "translate", "hello world"], CancellationToken.None);

        Assert.Equal(MonzeMessages.AiProviderEmpty, Assert.Single(failed.Fields!).Value);
        Assert.Equal((ClanId, UserId, 3), Assert.Single(dependencies.AiRefunds));

        dependencies.AiHandler = static (_, _, _) => throw new HttpRequestException("down");
        await Assert.ThrowsAsync<HttpRequestException>(() => app.HandleMonzeAsync(ClanId, ChannelId, UserId, ["ai", "translate", "hello world"], CancellationToken.None));
        Assert.Equal(2, dependencies.AiRefunds.Count);

        dependencies.AiHandler = null;
        dependencies.AiResponse = "xin chào";
        await app.HandleMonzeAsync(ClanId, ChannelId, UserId, ["ai", "translate", "hello world"], CancellationToken.None);
        Assert.Equal(2, dependencies.AiRefunds.Count);
    }

    [Fact]
    public async Task Ai_summary_combines_history_reports_gap_and_releases_concurrency_slot()
    {
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencies = new MonzeAppTestDependencies
        {
            HistoryHasGap = true,
            AiHandler = (_, _, _) => gate.Task
        };
        var app = dependencies.CreateApp(
            withAi: true,
            aiOptions: new AiExecutionOptions(2_000, 8_000, 1));
        var first = app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["ai", "summary", "hiện tại"],
            CancellationToken.None,
            aiRequest: new AiRequestContext(99, "lịch sử"));

        await WaitUntilAsync(() => dependencies.AiProviderCalls == 1);
        var busy = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId + 1,
            ["ai", "simplify", "nội dung"],
            CancellationToken.None);
        Assert.Equal(MonzeMessages.AiBusy, busy.Text);

        gate.SetResult("đã tóm tắt");
        var completed = await first;
        Assert.Equal("lịch sử\nhiện tại", dependencies.LastAiInput);
        Assert.True(completed.HasGap);
        Assert.Contains(completed.Fields!, field => field.Name == "Lưu ý");

        dependencies.AiHandler = null;
        var next = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId + 1,
            ["ai", "simplify", "nội dung"],
            CancellationToken.None);
        Assert.NotEqual(MonzeMessages.AiBusy, next.Text);
        Assert.Equal(2, dependencies.AiProviderCalls);
    }

    [Fact]
    public async Task Ai_bounds_provider_output_before_building_the_message()
    {
        var dependencies = new MonzeAppTestDependencies
        {
            AiResponse = new string('x', 8_000)
        };
        var app = dependencies.CreateApp(withAi: true);

        var outcome = await app.HandleMonzeAsync(
            ClanId,
            ChannelId,
            UserId,
            ["ai", "composer", "nội dung"],
            CancellationToken.None);

        var field = Assert.Single(outcome.Fields!);
        Assert.InRange(field.Value.Length, 1, 4_000);
        Assert.EndsWith(" ...", field.Value, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the AI provider call.");
            await Task.Delay(10);
        }
    }
}
