namespace Monze.Domain;

public sealed record AutoRoleRule(
    long ClanId,
    long RoleId,
    RoleRuleKind Kind,
    string? ConditionValue,
    long Version);
