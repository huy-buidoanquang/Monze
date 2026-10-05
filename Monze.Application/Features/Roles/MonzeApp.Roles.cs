using System.Collections.Concurrent;
using System.Globalization;
using Monze.Application.Commands;
using Monze.Domain;

namespace Monze.Application;

public sealed partial class MonzeApp
{
    private const int DefaultTenureDays = 30;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _roleRuleGates = new();

    private async Task<CommandOutcome> RoleAsync(
        long clanId,
        long userId,
        CommandArguments rest,
        CancellationToken cancellationToken)
    {
        if (rest.Length == 0)
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Role, cancellationToken);
        }

        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.AdminOnly, title: MonzeMessages.TitleRole, tone: MonzeTone.Error);
        }

        if (rest.Length == 1
            && (rest[0].Equals(MonzeCommandActions.On, StringComparison.OrdinalIgnoreCase)
                || rest[0].Equals(MonzeCommandActions.Off, StringComparison.OrdinalIgnoreCase)))
        {
            var enabled = rest[0].Equals(MonzeCommandActions.On, StringComparison.OrdinalIgnoreCase);
            var changed = await _roles.SetRoleAutomationEnabledAsync(
                clanId,
                userId,
                enabled,
                cancellationToken);
            if (!changed)
            {
                return Say(MonzeMessages.AdminOnly, title: MonzeMessages.TitleRole, tone: MonzeTone.Error);
            }
            return Say(
                enabled ? MonzeMessages.RoleAutomationEnabled : MonzeMessages.RoleAutomationDisabled,
                title: MonzeMessages.TitleRole,
                tone: MonzeTone.Ok);
        }

        if (rest.Length < 2
            || (!rest[0].Equals("join", StringComparison.OrdinalIgnoreCase)
                && !rest[0].Equals(MonzeCommandActions.Tenure, StringComparison.OrdinalIgnoreCase)))
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Role, cancellationToken);
        }

        var kind = rest[0].Equals("join", StringComparison.OrdinalIgnoreCase)
            ? RoleRuleKind.OnJoin
            : RoleRuleKind.Tenure;
        var remove = rest.Length >= 2
            && rest[1].Equals(MonzeCommandActions.Remove, StringComparison.OrdinalIgnoreCase);
        var selectorStart = remove ? 2 : 1;
        if (rest.Length <= selectorStart)
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Role, cancellationToken);
        }

        var role = await ResolveRoleAsync(
            clanId,
            rest.Join(' ', selectorStart),
            cancellationToken);
        if (!role.Found)
        {
            return Say(MonzeMessages.RoleNotFound, title: MonzeMessages.TitleRole, tone: MonzeTone.Warn);
        }

        if (remove)
        {
            var removed = await _roles.RemoveRoleRuleAsync(
                clanId,
                userId,
                role.RoleId,
                kind,
                cancellationToken);
            return Say(
                removed ? $"Đã xóa rule {RoleRuleLabel(kind)} cho role {role.Label}." : MonzeMessages.RoleRuleNotFound,
                title: MonzeMessages.TitleRole,
                tone: removed ? MonzeTone.Ok : MonzeTone.Warn);
        }

        var condition = kind == RoleRuleKind.Tenure
            ? DefaultTenureDays.ToString(CultureInfo.InvariantCulture)
            : null;
        var saved = await _roles.SetRoleRuleAsync(
            clanId,
            userId,
            role.RoleId,
            kind,
            condition,
            cancellationToken);
        if (!saved)
        {
            return Say(MonzeMessages.AdminOnly, title: MonzeMessages.TitleRole, tone: MonzeTone.Error);
        }
        return Say(
            $"Đã lưu rule {RoleRuleLabel(kind)} cho role {role.Label}.",
            title: MonzeMessages.TitleRole,
            tone: MonzeTone.Ok);
    }

    public async Task ApplyOnJoinRoleRulesAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        var gateway = _roleGateway;
        if (gateway is null || !await _roles.IsRoleAutomationEnabledAsync(clanId, cancellationToken))
        {
            return;
        }

        var rules = await _roles.ListEnabledRoleRulesAsync(clanId, cancellationToken);
        if (rules.Count == 0)
        {
            return;
        }

        var gate = _roleRuleGates.GetOrAdd(clanId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var members = await gateway.ListMembersAsync(clanId, cancellationToken);
            for (var i = 0; i < members.Count; i++)
            {
                if (members[i].UserId == userId)
                {
                    await ApplyRulesToMemberAsync(clanId, members[i], rules, true, gateway, cancellationToken);
                    return;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ApplyAutomaticRoleRulesAsync(CancellationToken cancellationToken)
    {
        var gateway = _roleGateway;
        if (gateway is null)
        {
            return;
        }

        var rules = await _roles.ListEnabledRoleRulesAsync(cancellationToken);
        if (rules.Count == 0)
        {
            return;
        }

        var byClan = new Dictionary<long, List<AutoRoleRule>>();
        for (var i = 0; i < rules.Count; i++)
        {
            if (!byClan.TryGetValue(rules[i].ClanId, out var clanRules))
            {
                clanRules = new List<AutoRoleRule>();
                byClan.Add(rules[i].ClanId, clanRules);
            }

            clanRules.Add(rules[i]);
        }

        foreach (var pair in byClan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyAutomaticRoleRulesForClanAsync(pair.Key, pair.Value, gateway, cancellationToken);
        }
    }

    private async Task ApplyAutomaticRoleRulesForClanAsync(
        long clanId,
        IReadOnlyList<AutoRoleRule> rules,
        IMezonRoleGateway gateway,
        CancellationToken cancellationToken)
    {
        if (!await _roles.IsRoleAutomationEnabledAsync(clanId, cancellationToken))
        {
            return;
        }

        var gate = _roleRuleGates.GetOrAdd(clanId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var members = await gateway.ListMembersAsync(clanId, cancellationToken);
            for (var i = 0; i < members.Count; i++)
            {
                await ApplyRulesToMemberAsync(clanId, members[i], rules, false, gateway, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ApplyRulesToMemberAsync(
        long clanId,
        MemberRoleSnapshot member,
        IReadOnlyList<AutoRoleRule> rules,
        bool onJoinOnly,
        IMezonRoleGateway gateway,
        CancellationToken cancellationToken)
    {
        if (member.IsBot)
        {
            return;
        }

        HashSet<long>? assignedRoleIds = null;
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if ((onJoinOnly && rule.Kind != RoleRuleKind.OnJoin)
                || rule.Kind is not (RoleRuleKind.OnJoin or RoleRuleKind.Tenure)
                || member.RoleIds.Contains(rule.RoleId)
                || (assignedRoleIds is not null && assignedRoleIds.Contains(rule.RoleId))
                || !MatchesAutomaticRule(rule, member))
            {
                continue;
            }

            var result = await gateway.AddUserToRoleAsync(clanId, rule.RoleId, member.UserId, cancellationToken);
            if (!result.Succeeded)
            {
                continue;
            }

            assignedRoleIds ??= new HashSet<long>();
            assignedRoleIds.Add(rule.RoleId);
            await _roles.RecordRoleGrantAsync(clanId, rule.RoleId, member.UserId, cancellationToken);
        }
    }

    private static bool MatchesAutomaticRule(AutoRoleRule rule, MemberRoleSnapshot member)
        => rule.Kind switch
        {
            RoleRuleKind.OnJoin => true,
            RoleRuleKind.Tenure => member.JoinedAt is { } joinedAt
                && TryReadPositiveLong(rule.ConditionValue, out var days)
                && RoleRules.MatchesTenure(joinedAt, DateTimeOffset.UtcNow, TimeSpan.FromDays(days)),
            _ => false
        };

    private async Task<RoleResolutionResult> ResolveRoleAsync(
        long clanId,
        string selector,
        CancellationToken cancellationToken)
        => _roleGateway is null
            ? new RoleResolutionResult(false, 0, string.Empty)
            : await _roleGateway.ResolveRoleAsync(clanId, selector, cancellationToken);

    private static string RoleRuleLabel(RoleRuleKind kind)
        => kind == RoleRuleKind.Tenure ? "tenure" : "join";

    private static bool TryReadPositiveLong(string? value, out long result)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result > 0;
}
