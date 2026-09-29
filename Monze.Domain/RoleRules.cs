namespace Monze.Domain;

public static class RoleRules
{
    public static string ToStorageName(RoleRuleKind kind)
        => kind switch
        {
            RoleRuleKind.OnJoin => "on_join",
            RoleRuleKind.SelfSelect => "self_select",
            RoleRuleKind.ExistingRole => "existing_role",
            RoleRuleKind.Tenure => "tenure",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    public static bool TryParseKind(string value, out RoleRuleKind kind)
    {
        kind = value.Trim().ToLowerInvariant() switch
        {
            "join" or "on_join" => RoleRuleKind.OnJoin,
            "self" or "self_select" => RoleRuleKind.SelfSelect,
            "existing" or "existing_role" => RoleRuleKind.ExistingRole,
            "tenure" => RoleRuleKind.Tenure,
            _ => (RoleRuleKind)(-1)
        };

        return Enum.IsDefined(kind);
    }

    public static bool MatchesTenure(DateTimeOffset joinedAt, DateTimeOffset now, TimeSpan minimum)
        => now - joinedAt >= minimum;

    public static bool MatchesExistingRole(IReadOnlySet<long> roleIds, long requiredRoleId)
        => roleIds.Contains(requiredRoleId);
}
