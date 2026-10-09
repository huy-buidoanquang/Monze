namespace Monze.Domain;

/// <param name="EffectiveSince">
/// When the rule was last set (role_rule.updated_at): an on-join rule only
/// applies to members who joined from then on.
/// </param>
public sealed record AutoRoleRule(
    long ClanId,
    long RoleId,
    RoleRuleKind Kind,
    string? ConditionValue,
    long Version,
    DateTimeOffset? EffectiveSince = null);
