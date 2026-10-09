using Mezon.Net.Core;
using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Areas;

/// <summary>
/// Roles: admins switch automation and rules; an on-join rule assigns the
/// role to a member who joins (through UpdateRole); the tenure scan assigns
/// it once the member's platform join time passes 30 days and never again to
/// a holder; a failed UpdateRole grants nothing and is retried by the next
/// scan. Tenure is measured from the Mezon join time (ListClanUsers), so the
/// time warp moves the simulated member's join time back.
/// </summary>
public sealed class RoleAreaTests
{
    [DbFact]
    [Req("REQ-ROLE-001")]
    [Covers("cmd:role", "msg:RoleAutomationEnabled", "msg:RoleAutomationDisabled", "msg:AdminOnly", "msg:TitleRole")]
    public async Task Rules_switch_on_and_off_and_a_join_rule_assigns_the_role_to_the_joiner()
    {
        const long LateJoinerId = 1_840_000_000_000_000_298L;
        // No periodic scan during the test: only the on-join path may assign.
        await using var host = await E2EActions.StartAsync("role_join", timings: static timings => timings with { RoleScanInterval = TimeSpan.FromHours(1) });
        host.World.User(LateJoinerId, "late-joiner", "Late Joiner");
        var mark = await E2EOracles.MarkAsync(host);
        var rule = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*role join Developer");
        var on = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*role on");
        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of((rule, ResponseKind.Reply), (on, ResponseKind.Reply)));
        Assert.Contains("Đã lưu rule join cho role Developer.", Answer(host, rule));
        Assert.Contains(MonzeMessages.RoleAutomationEnabled, Answer(host, on));
        Assert.True(await host.ScalarAsync<bool>("SELECT role_enabled FROM clan_settings WHERE clan_id = @clan;", ("clan", ClanId)));
        Assert.Equal("on_join", await host.ScalarAsync<string>(
            "SELECT rule_kind FROM role_rule WHERE clan_id = @clan AND role_id = @role AND enabled;",
            ("clan", ClanId),
            ("role", DeveloperRoleId)));

        var join = await E2EOracles.MarkAsync(host);
        await host.Inbound.UserAddedAsync(ClanId, JoinerId);
        await host.Recorder.WaitForAsync(static action => action.Kind == SimActionKind.RoleAssignment, E2EOracles.Timeout, join.Sequence);
        await E2EActions.WaitForLogsAsync(host, static entry => entry.Message.StartsWith("Welcome event ignored because welcome is disabled", StringComparison.Ordinal), 1);
        await E2EOracles.AssertAsync(host, join, new ScenarioExpectation());
        var assignment = Assert.Single(host.Recorder.Since(join.Sequence), static action => action.Kind == SimActionKind.RoleAssignment);
        Assert.Equal((DeveloperRoleId, ClanId, 0), (assignment.RoleId, assignment.ClanId, assignment.ResponseCode));
        Assert.Equal(new[] { JoinerId }, assignment.AddedUserIds);
        Assert.Contains(DeveloperRoleId, host.World.FindMember(ClanId, JoinerId)!.RoleIds);
        Assert.Equal(new[] { JoinerId }, await GrantsAsync(host, DeveloperRoleId));

        var off = await E2EOracles.MarkAsync(host);
        var disable = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*role off");
        await host.Inbound.UserAddedAsync(ClanId, LateJoinerId);
        await E2EActions.WaitForLogsAsync(host, static entry => entry.Message.StartsWith("Welcome event ignored because welcome is disabled", StringComparison.Ordinal), 2);
        var remove = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*role join remove Developer");
        await E2EOracles.AssertAsync(host, off, ScenarioExpectation.Of((disable, ResponseKind.Reply), (remove, ResponseKind.Reply)));
        Assert.Contains(MonzeMessages.RoleAutomationDisabled, Answer(host, disable));
        Assert.Contains("Đã xóa rule join cho role Developer.", Answer(host, remove));
        Assert.DoesNotContain(host.Recorder.Since(off.Sequence), static action => action.Kind == SimActionKind.RoleAssignment);
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM role_rule WHERE clan_id = @clan;", ("clan", ClanId)));

        var refused = await E2EOracles.MarkAsync(host, snapshot: true);
        var memberOn = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*role on");
        var memberRule = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*role tenure Veteran");
        await E2EOracles.AssertAsync(host, refused, new ScenarioExpectation
        {
            Inputs = ScenarioExpectation.Of((memberOn, ResponseKind.Reply), (memberRule, ResponseKind.Reply)).Inputs,
            Unauthorized = true
        });
        Assert.Contains(MonzeMessages.AdminOnly, Answer(host, memberOn));
        Assert.Contains(MonzeMessages.AdminOnly, Answer(host, memberRule));
    }

    [DbFact]
    [Req("REQ-ROLE-002")]
    [Covers("cmd:role")]
    public async Task Tenure_scan_assigns_after_the_threshold_and_never_to_a_holder()
    {
        await using var host = await E2EActions.StartAsync("role_tenure");
        var mark = await E2EOracles.MarkAsync(host);
        var rule = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*role tenure Veteran");
        var on = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*role on");

        // Member joined 40 days ago; member2 1 day ago; the admin holds the role already.
        var first = await host.Recorder.WaitForAsync(static action => action.Kind == SimActionKind.RoleAssignment, E2EOracles.Timeout, mark.Sequence);
        await ScanQuietAsync(host);
        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of((rule, ResponseKind.Reply), (on, ResponseKind.Reply)));
        Assert.Contains("Đã lưu rule tenure cho role Veteran.", Answer(host, rule));
        Assert.Equal(new[] { MemberId }, first.AddedUserIds);
        Assert.Single(host.Recorder.Since(mark.Sequence), static action => action.Kind == SimActionKind.RoleAssignment);
        Assert.Equal(new[] { MemberId }, await GrantsAsync(host, VeteranRoleId));

        var warp = await E2EOracles.MarkAsync(host);
        host.World.AddMember(ClanId, Member2Id, joinedAt: DateTimeOffset.UtcNow.AddDays(-31));
        var second = await host.Recorder.WaitForAsync(static action => action.Kind == SimActionKind.RoleAssignment, E2EOracles.Timeout, warp.Sequence);
        await ScanQuietAsync(host);
        await E2EOracles.AssertAsync(host, warp, new ScenarioExpectation());
        Assert.Equal(new[] { Member2Id }, second.AddedUserIds);
        Assert.Single(host.Recorder.Since(warp.Sequence), static action => action.Kind == SimActionKind.RoleAssignment);
        Assert.Equal(new[] { MemberId, Member2Id }, await GrantsAsync(host, VeteranRoleId));
        Assert.DoesNotContain(host.Recorder.Actions, static action => action.Kind == SimActionKind.RoleAssignment && action.AddedUserIds.Contains(AdminId));
    }

    [DbFact]
    [Req("REQ-ROLE-002")]
    [Covers("msg:RoleAssignFailed")]
    public async Task A_failed_role_update_grants_nothing_and_the_next_scan_retries()
    {
        await using var host = await E2EActions.StartAsync("role_fail");
        host.Simulator.Faults.Fail(SimOperations.UpdateRole, MezonStatusCode.PermissionDenied, detail: "bot lacks manage-roles");
        var mark = await E2EOracles.MarkAsync(host);
        var rule = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*role tenure Veteran");
        var on = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*role on");

        var attempts = await host.Recorder.WaitForCountAsync(static action => action.Kind == SimActionKind.RoleAssignment, 2, E2EOracles.Timeout, mark.Sequence);
        await ScanQuietAsync(host);
        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation
        {
            Inputs = ScenarioExpectation.Of((rule, ResponseKind.Reply), (on, ResponseKind.Reply)).Inputs,
            AllowedWarnings = ["Role assignment API failed"]
        });
        Assert.Equal((int)MezonStatusCode.PermissionDenied, attempts[0].ResponseCode);
        Assert.Equal(0, attempts[1].ResponseCode);
        Assert.All(attempts, static attempt => Assert.Equal(new[] { MemberId }, attempt.AddedUserIds));
        Assert.Contains(host.Logs.Entries, static entry => entry.Message.StartsWith("Role assignment API failed", StringComparison.Ordinal) && entry.Exception is not null);
        Assert.Equal(new[] { MemberId }, await GrantsAsync(host, VeteranRoleId));

        // MonzeMessages.RoleAssignFailed is never sent: a failed automatic
        // assignment is only logged (Features/Roles/SdkRoleGateway.cs).
        Assert.DoesNotContain(
            host.Recorder.Actions.Where(static action => action.ContentJson is not null),
            static action => E2EContent.Visible(action).Contains(MonzeMessages.RoleAssignFailed, StringComparison.Ordinal));
    }

    /// <summary>
    /// Regression for CAND-23 (Monze's part): the periodic scan applies a join
    /// rule to the members it lists, and ListClanUsers carries no bot flag, so
    /// Monze used to grant the role to itself. It now skips its own account;
    /// other bots still need the platform's flag (docs/mezon-net-handoff.md).
    /// </summary>
    [DbFact]
    [Req("REQ-ROLE-001")]
    public async Task The_periodic_scan_never_grants_a_role_to_monze_itself()
    {
        await using var host = await E2EActions.StartAsync("role_self");
        var mark = await E2EOracles.MarkAsync(host);
        var rule = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*role join Developer");
        var on = await E2EActions.CommandAsync(host, GeneralId, OwnerId, "*role on");

        // An on-join rule is for members who join after it is set (CAND-22): Monze and a member
        // join after it here without a join event (joins the bot missed), which the scan catches up on.
        foreach (var userId in new[] { Member2Id, BotId })
        {
            var member = host.World.FindMember(ClanId, userId)!;
            host.World.AddMember(ClanId, userId, member.ClanNick, DateTimeOffset.UtcNow.AddMinutes(1), member.RoleIds);
        }

        // The scan grants member by member; let it finish before checking that it stays quiet.
        await host.Recorder.WaitForAsync(static action => action.Kind == SimActionKind.RoleAssignment, E2EOracles.Timeout, mark.Sequence);
        await host.Recorder.WaitForQuietAsync(TimeSpan.FromSeconds(1.5), E2EOracles.Timeout);
        await ScanQuietAsync(host);
        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of((rule, ResponseKind.Reply), (on, ResponseKind.Reply)));
        var granted = host.Recorder.Since(mark.Sequence)
            .Where(static action => action.Kind == SimActionKind.RoleAssignment)
            .SelectMany(static action => action.AddedUserIds)
            .ToList();
        Assert.Equal(new[] { Member2Id }, granted.Distinct());
        Assert.DoesNotContain(BotId, granted);
        Assert.DoesNotContain(BotId, await GrantsAsync(host, DeveloperRoleId));
    }

    /// <summary>Two scan intervals without a new assignment.</summary>
    private static async Task ScanQuietAsync(MonzeE2EHost host)
    {
        var before = host.Recorder.Actions.Count(static action => action.Kind == SimActionKind.RoleAssignment);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Equal(before, host.Recorder.Actions.Count(static action => action.Kind == SimActionKind.RoleAssignment));
    }

    private static string Answer(MonzeE2EHost host, SimPush command)
        => E2EContent.Visible(E2EActions.NewMessageAfter(host, command, GeneralId));

    private static async Task<IReadOnlyList<long>> GrantsAsync(MonzeE2EHost host, long roleId)
        => (await host.RowsAsync(
                "SELECT user_id FROM role_grant WHERE clan_id = @clan AND role_id = @role ORDER BY user_id;",
                ("clan", ClanId),
                ("role", roleId)))
            .Select(static row => (long)row[0]!)
            .ToList();
}
