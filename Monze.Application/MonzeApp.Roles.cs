using System.Globalization;
using System.Collections.Concurrent;
using Monze.Application.Commands;
using Monze.Domain;

namespace Monze.Application;

public sealed partial class MonzeApp
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _roleRuleGates = new();

    private async Task<CommandOutcome> ConfigureRoleRuleAsync(
        long clanId,
        long userId,
        CommandArguments rest,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.AdminOnly);
        }

        if (rest.Length < 3)
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Role, cancellationToken);
        }

        var action = rest[1].Trim();
        if (action.Equals(MonzeCommandActions.Remove, StringComparison.OrdinalIgnoreCase))
        {
            if (rest.Length < 4 || !RoleRules.TryParseKind(rest[2], out var removeKind))
            {
                return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Role, cancellationToken);
            }

            var removeRole = await ResolveRoleAsync(clanId, rest.Join(' ', 3), cancellationToken);
            if (!removeRole.Found || removeKind == RoleRuleKind.SelfSelect)
            {
                return Say(MonzeMessages.RoleRuleInvalid);
            }

            var removed = await _authorization.RemoveRoleRuleAsync(
                clanId,
                removeRole.RoleId,
                removeKind,
                cancellationToken);
            return Say(removed ? $"Đã xóa rule {removeKind} cho role {removeRole.Label}." : "Không tìm thấy rule role.");
        }

        if (!RoleRules.TryParseKind(action, out var kind) || kind == RoleRuleKind.SelfSelect)
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Role, cancellationToken);
        }

        var raw = rest.Join(' ', 2);
        var parts = raw.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return Say(MonzeMessages.RoleRuleInvalid);
        }

        var targetRole = await ResolveRoleAsync(clanId, parts[0], cancellationToken);
        if (!targetRole.Found)
        {
            return Say(MonzeMessages.RoleNotFound);
        }

        string? condition = null;
        if (kind == RoleRuleKind.ExistingRole)
        {
            if (parts.Length != 2)
            {
                return Say(MonzeMessages.RoleRuleInvalid);
            }

            var requiredRole = await ResolveRoleAsync(clanId, parts[1], cancellationToken);
            if (!requiredRole.Found)
            {
                return Say(MonzeMessages.RoleNotFound);
            }

            condition = requiredRole.RoleId.ToString(CultureInfo.InvariantCulture);
        }
        else if (kind is RoleRuleKind.Tenure or RoleRuleKind.MinPoints)
        {
            if (parts.Length != 2
                || !long.TryParse(
                    parts[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value)
                || value <= 0)
            {
                return Say(MonzeMessages.RoleRuleInvalid);
            }

            condition = value.ToString(CultureInfo.InvariantCulture);
        }
        else if (parts.Length != 1)
        {
            return Say(MonzeMessages.RoleRuleInvalid);
        }

        await _authorization.SetRoleRuleAsync(
            clanId,
            targetRole.RoleId,
            kind,
            condition,
            cancellationToken);
        return Say($"Đã lưu rule {kind} cho role {targetRole.Label}.");
    }

    public async Task ApplyOnJoinRoleRulesAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        var gateway = _roleGateway;
        if (gateway is null)
        {
            return;
        }

        var rules = await _authorization.ListEnabledRoleRulesAsync(clanId, cancellationToken);
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
                if (members[i].UserId != userId)
                {
                    continue;
                }

                await ApplyRulesToMemberAsync(
                    clanId,
                    members[i],
                    rules,
                    pointsByMinimum: null,
                    onJoinOnly: true,
                    gateway,
                    cancellationToken);
                return;
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

        var rules = await _authorization.ListEnabledRoleRulesAsync(cancellationToken);
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
            await ApplyAutomaticRoleRulesForClanAsync(
                pair.Key,
                pair.Value,
                gateway,
                cancellationToken);
        }
    }

    private async Task ApplyAutomaticRoleRulesForClanAsync(
        long clanId,
        IReadOnlyList<AutoRoleRule> rules,
        IMezonRoleGateway gateway,
        CancellationToken cancellationToken)
    {
        var gate = _roleRuleGates.GetOrAdd(clanId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var members = await gateway.ListMembersAsync(clanId, cancellationToken);
            var pointsByMinimum = new Dictionary<long, IReadOnlySet<long>>();
            for (var i = 0; i < rules.Count; i++)
            {
                if (rules[i].Kind != RoleRuleKind.MinPoints
                    || !TryReadPositiveLong(rules[i].ConditionValue, out var minimum)
                    || pointsByMinimum.ContainsKey(minimum))
                {
                    continue;
                }

                var users = await _community.UsersWithMinimumPointsAsync(
                    clanId,
                    minimum,
                    cancellationToken);
                pointsByMinimum[minimum] = users.ToHashSet();
            }

            for (var i = 0; i < members.Count; i++)
            {
                await ApplyRulesToMemberAsync(
                    clanId,
                    members[i],
                    rules,
                    pointsByMinimum,
                    onJoinOnly: false,
                    gateway,
                    cancellationToken);
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
        IReadOnlyDictionary<long, IReadOnlySet<long>>? pointsByMinimum,
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
            if (rule.Kind == RoleRuleKind.SelfSelect
                || (onJoinOnly && rule.Kind is not (RoleRuleKind.OnJoin or RoleRuleKind.ExistingRole))
                || member.RoleIds.Contains(rule.RoleId)
                || (assignedRoleIds is not null && assignedRoleIds.Contains(rule.RoleId))
                || !MatchesAutomaticRule(rule, member, pointsByMinimum))
            {
                continue;
            }

            var result = await gateway.AddUserToRoleAsync(
                clanId,
                rule.RoleId,
                member.UserId,
                cancellationToken);
            if (!result.Succeeded)
            {
                continue;
            }

            assignedRoleIds ??= new HashSet<long>();
            assignedRoleIds.Add(rule.RoleId);
            await _authorization.RecordRoleGrantAsync(
                clanId,
                rule.RoleId,
                member.UserId,
                cancellationToken);
        }
    }

    private static bool MatchesAutomaticRule(
        AutoRoleRule rule,
        MemberRoleSnapshot member,
        IReadOnlyDictionary<long, IReadOnlySet<long>>? pointsByMinimum)
    {
        return rule.Kind switch
        {
            RoleRuleKind.OnJoin => true,
            RoleRuleKind.ExistingRole => TryReadPositiveLong(rule.ConditionValue, out var requiredRoleId)
                && RoleRules.MatchesExistingRole(member.RoleIds, requiredRoleId),
            RoleRuleKind.Tenure => member.JoinedAt is { } joinedAt
                && TryReadPositiveLong(rule.ConditionValue, out var days)
                && RoleRules.MatchesTenure(joinedAt, DateTimeOffset.UtcNow, TimeSpan.FromDays(days)),
            RoleRuleKind.MinPoints => TryReadPositiveLong(rule.ConditionValue, out var minimum)
                && pointsByMinimum is not null
                && pointsByMinimum.TryGetValue(minimum, out var users)
                && users.Contains(member.UserId),
            _ => false
        };
    }

    private async Task<RoleResolutionResult> ResolveRoleAsync(
        long clanId,
        string selector,
        CancellationToken cancellationToken)
    {
        if (_roleGateway is null)
        {
            return new RoleResolutionResult(false, 0, string.Empty);
        }

        return await _roleGateway.ResolveRoleAsync(clanId, selector, cancellationToken);
    }

    private static bool TryReadPositiveLong(string? value, out long result)
        => long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out result)
            && result > 0;
}
