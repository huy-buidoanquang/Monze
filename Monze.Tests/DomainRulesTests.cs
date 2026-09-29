using Monze.Domain;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Infrastructure.Persistence;
using Xunit;

namespace Monze.Tests;

public class DomainRulesTests
{
    [Fact]
    public void Capped_clan_list_does_not_insert_or_drop()
    {
        var known = new[] { new KnownClan(1, 10) };
        var listed = new[] { new ClanScanItem(2, 20) };
        var merged = ClanDiscovery.Merge(known, listed, listLooksCapped: true);
        Assert.Equal(ClanScanDisposition.IncompleteList, merged[0].Disposition);
        Assert.DoesNotContain(merged, item => item.Disposition == ClanScanDisposition.Inserted);
    }

    [Fact]
    public void Complete_clan_list_can_mark_missing_registry_rows_inactive()
    {
        Assert.True(ClanDiscovery.IsKnownMissing(
            new KnownClan(9, 10),
            new HashSet<long> { 1, 2 },
            listLooksCapped: false));
        Assert.False(ClanDiscovery.IsKnownMissing(
            new KnownClan(9, 10),
            new HashSet<long> { 1, 2 },
            listLooksCapped: true));
    }

    [Fact]
    public void Empty_discovery_does_not_deactivate_known_clans()
    {
        Assert.True(ClanDiscovery.ListLooksIncomplete(0, 1));
        Assert.False(ClanDiscovery.ListLooksIncomplete(0, 0));
    }

    [Fact]
    public void Meeting_summary_is_posted_once_from_live()
    {
        Assert.Equal(MeetingStatus.Suggested, MeetingFlow.Suggest(MeetingStatus.Requested));
        Assert.True(MeetingFlow.TryBindRoom(MeetingStatus.Suggested, true, out var live));
        Assert.Equal(MeetingStatus.Live, live);
        Assert.False(MeetingFlow.TryBindRoom(MeetingStatus.Requested, true, out _));
        Assert.Equal(MeetingStatus.Posted, MeetingFlow.OnSummaryStored(MeetingStatus.Live));
        Assert.Equal(MeetingStatus.SummaryPending, MeetingFlow.OnSummaryFailed(MeetingStatus.Live));
    }

    [Theory]
    [InlineData(OutboxKind.Announcement, false, 1, OutboxAction.HoldForAdmin)]
    [InlineData(OutboxKind.MeetingSummary, true, 0, OutboxAction.AlreadyDelivered)]
    public void Outbox_policy(OutboxKind kind, bool hasId, int attempts, OutboxAction expected)
        => Assert.Equal(expected, OutboxPolicy.Decide(kind, hasId, attempts));

    [Fact]
    public void Meeting_parser_accepts_now()
    {
        Assert.True(MeetingCommandParser.TryParse(["now"], out var request));
        Assert.Equal(MeetingScheduleKind.Now, request!.Kind);
        Assert.False(MeetingCommandParser.TryParse(["later"], out _));
    }

    [Fact]
    public void Meeting_parser_is_case_insensitive_without_normalizing_input()
    {
        Assert.True(MeetingCommandParser.TryParse(["DAILY", "09:00", "local"], out var request));
        Assert.Equal(MeetingScheduleKind.Daily, request!.Kind);
        Assert.Equal("09:00 local", request.WhenText);
    }

    [Fact]
    public void Meeting_parser_rejects_removed_repeat_syntax()
    {
        Assert.False(MeetingCommandParser.TryParse(
            ["Sprint", "Review", "28/09/2026", "18:30", "repeat", "30"],
            out _));
    }

    [Theory]
    [InlineData("once", MeetingScheduleKind.Once)]
    [InlineData("daily", MeetingScheduleKind.Daily)]
    [InlineData("weekly", MeetingScheduleKind.Weekly)]
    public void Meeting_parser_accepts_named_schedule_suffix(string suffix, MeetingScheduleKind expected)
    {
        Assert.True(MeetingCommandParser.TryParse(
            ["Team", "Sync", "28/09/2026", "18:30", suffix],
            out var request));

        Assert.Equal(expected, request!.Kind);
    }

    [Fact]
    public void Meeting_parser_accepts_cancel_id_and_rejects_invalid_ids()
    {
        Assert.True(MeetingCommandParser.TryParse(["cancel", "42"], out var request));
        Assert.Equal(42, request!.CancelScheduleId);
        Assert.False(MeetingCommandParser.TryParse(["cancel", "0"], out _));
        Assert.False(MeetingCommandParser.TryParse(["cancel", "abc"], out _));
    }

    [Fact]
    public void Meeting_schedule_calculator_accepts_vietnamese_date_format()
    {
        var now = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
        var ok = MeetingScheduleCalculator.TryGetNext(
            MeetingScheduleKind.Once,
            "29/09/2026 18:30",
            "Asia/Ho_Chi_Minh",
            now,
            out var next,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(11, next.Hour);
        Assert.Equal(30, next.Minute);
    }

    [Fact]
    public void Daily_meeting_schedule_uses_future_local_time()
    {
        var now = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);
        var ok = MeetingScheduleCalculator.TryGetNext(
            MeetingScheduleKind.Daily,
            "18:00",
            "Asia/Ho_Chi_Minh",
            now,
            out var next,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(11, next.Hour);
        Assert.True(next > now);
    }

    [Fact]
    public void Once_meeting_schedule_rejects_past_time()
    {
        var ok = MeetingScheduleCalculator.TryGetNext(
            MeetingScheduleKind.Once,
            "2020-01-01 18:00",
            "Asia/Ho_Chi_Minh",
            DateTimeOffset.UtcNow,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("tương lai", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ai_budget_stops_at_cap()
    {
        Assert.False(AiBudget.TryConsume(1990, 20, 2000, out _));
        Assert.True(AiBudget.TryConsume(10, 5, 2000, out var used));
        Assert.Equal(15, used);
    }

    [Fact]
    public void Ai_execution_limits_are_normalized()
    {
        var options = new AiExecutionOptions(0, 1, 999).Normalize();

        Assert.Equal(1, options.DailyTokenCap);
        Assert.Equal(256, options.MaxInputCharacters);
        Assert.Equal(128, options.MaxConcurrentRequests);
    }

    [Fact]
    public void Gap_note_is_explicit()
    {
        Assert.Contains("thiếu", MessageGap.CoverageNote(true), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, MessageGap.CoverageNote(false));
    }

    [Fact]
    public void Maintenance_connection_keeps_the_password()
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder("Host=127.0.0.1;Database=monze_new;Username=postgres;Password=abc#def");
        builder.Database = "postgres";
        builder.PersistSecurityInfo = true;
        var roundTrip = new Npgsql.NpgsqlConnectionStringBuilder(builder.ConnectionString);
        Assert.Equal("postgres", roundTrip.Database);
        Assert.Equal("abc#def", roundTrip.Password);
    }

    [Fact]
    public void Database_name_is_quoted_for_create_database()
    {
        Assert.Equal("\"monze\"", PostgresMigrator.QuoteIdentifier("monze"));
        Assert.Equal("\"monze\"\"db\"", PostgresMigrator.QuoteIdentifier("monze\"db"));
    }

    [Fact]
    public void Ho_Chi_Minh_converts_without_dst_gap()
    {
        var ok = LocalSchedule.TryToUtc(new DateOnly(2026, 9, 26), new TimeOnly(18, 0), "Asia/Ho_Chi_Minh", out var utc, out var note);
        Assert.True(ok);
        Assert.Null(note);
        Assert.Equal(11, utc.Hour);
    }

    [Fact]
    public void Command_options_can_remove_the_monze_root()
    {
        var options = new MonzeCommandOptions("*", null);

        Assert.Equal("*role", options.Command(MonzeCommandNames.Role));
        Assert.Equal("*help", options.HelpCommand);
    }

    [Fact]
    public void Command_options_can_remove_the_prefix()
    {
        var options = new MonzeCommandOptions(string.Empty, null);

        Assert.Equal("role", options.Command(MonzeCommandNames.Role));
        Assert.Equal("help", options.HelpCommand);
    }

    [Fact]
    public void Command_rate_limiter_is_scoped_to_clan_and_user()
    {
        var limiter = new MonzeCommandRateLimiter(new MonzeRateLimitOptions(
            UserLimit: 1,
            UserWindow: TimeSpan.FromMinutes(1),
            AiLimit: 1,
            AiWindow: TimeSpan.FromMinutes(1),
            MeetingLimit: 1,
            MeetingWindow: TimeSpan.FromMinutes(1),
            AdminLimit: 1,
            AdminWindow: TimeSpan.FromMinutes(1),
            MaxEntries: 16));
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire(1, 10, MonzeCommandNames.Role, now, out _));
        Assert.False(limiter.TryAcquire(1, 10, MonzeCommandNames.Role, now, out var retry));
        Assert.True(retry > TimeSpan.Zero);
        Assert.True(limiter.TryAcquire(2, 10, MonzeCommandNames.Role, now, out _));
    }

    [Fact]
    public void Command_rate_limiter_removes_expired_entry_before_capacity_check()
    {
        var limiter = new MonzeCommandRateLimiter(new MonzeRateLimitOptions(
            UserLimit: 1,
            UserWindow: TimeSpan.FromSeconds(10),
            AiLimit: 1,
            AiWindow: TimeSpan.FromSeconds(10),
            MeetingLimit: 1,
            MeetingWindow: TimeSpan.FromSeconds(10),
            AdminLimit: 1,
            AdminWindow: TimeSpan.FromSeconds(10),
            MaxEntries: 1));
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire(1, 1, MonzeCommandNames.Role, now, out _));
        Assert.True(limiter.TryAcquire(2, 2, MonzeCommandNames.Role, now.AddSeconds(11), out _));
    }

    [Fact]
    public void Command_rate_limiter_isolated_by_command_within_same_policy_group()
    {
        var limiter = new MonzeCommandRateLimiter(new MonzeRateLimitOptions(
            UserLimit: 2,
            UserWindow: TimeSpan.FromMinutes(1),
            AiLimit: 2,
            AiWindow: TimeSpan.FromMinutes(1),
            MeetingLimit: 2,
            MeetingWindow: TimeSpan.FromMinutes(1),
            AdminLimit: 1,
            AdminWindow: TimeSpan.FromMinutes(1),
            MaxEntries: 16));
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire(1, 10, MonzeCommandNames.Welcome, now, out _));
        Assert.True(limiter.TryAcquire(1, 10, MonzeCommandNames.Setup, now, out _));
        Assert.False(limiter.TryAcquire(1, 10, MonzeCommandNames.Welcome, now, out _));
    }

    [Fact]
    public void Command_rate_limiter_normalizes_command_before_selecting_policy()
    {
        var limiter = new MonzeCommandRateLimiter(new MonzeRateLimitOptions(
            UserLimit: 10,
            UserWindow: TimeSpan.FromMinutes(1),
            AiLimit: 10,
            AiWindow: TimeSpan.FromMinutes(1),
            MeetingLimit: 10,
            MeetingWindow: TimeSpan.FromMinutes(1),
            AdminLimit: 1,
            AdminWindow: TimeSpan.FromMinutes(1),
            MaxEntries: 16));
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire(1, 10, " WELCOME ", now, out _));
        Assert.False(limiter.TryAcquire(1, 10, "WELCOME", now, out _));
    }

    [Fact]
    public void Command_rate_limiter_reuses_typed_key_without_hotpath_allocation()
    {
        var limiter = new MonzeCommandRateLimiter(new MonzeRateLimitOptions(
            UserLimit: 10_000,
            UserWindow: TimeSpan.FromMinutes(1),
            AiLimit: 10_000,
            AiWindow: TimeSpan.FromMinutes(1),
            MeetingLimit: 10_000,
            MeetingWindow: TimeSpan.FromMinutes(1),
            AdminLimit: 10_000,
            AdminWindow: TimeSpan.FromMinutes(1),
            MaxEntries: 16));
        var now = DateTimeOffset.UtcNow;
        Assert.True(limiter.TryAcquire(1, 10, MonzeCommandNames.Role, now, out _));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 1000; i++)
        {
            Assert.True(limiter.TryAcquire(1, 10, MonzeCommandNames.Role, now, out _));
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Role_rule_aliases_and_conditions_are_explicit()
    {
        Assert.True(RoleRules.TryParseKind("join", out var join));
        Assert.Equal(RoleRuleKind.OnJoin, join);
        Assert.False(RoleRules.TryParseKind("points", out _));
        Assert.False(RoleRules.TryParseKind("min_points", out _));
        Assert.Equal("existing_role", RoleRules.ToStorageName(RoleRuleKind.ExistingRole));
        Assert.True(RoleRules.MatchesExistingRole(new HashSet<long> { 7, 11 }, 11));
        Assert.False(RoleRules.MatchesExistingRole(new HashSet<long> { 7 }, 11));
    }

    [Fact]
    public void Automatic_role_conditions_require_valid_positive_values()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(RoleRules.MatchesTenure(now.AddDays(-30), now, TimeSpan.FromDays(30)));
        Assert.False(RoleRules.MatchesTenure(now.AddDays(-29), now, TimeSpan.FromDays(30)));
    }
}
