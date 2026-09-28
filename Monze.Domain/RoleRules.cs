namespace Monze.Domain;

public static class RoleRules
{
    public static bool AllowedInWave1(RoleRuleKind kind) => kind != RoleRuleKind.MinPoints;

    public static string ToStorageName(RoleRuleKind kind)
        => kind switch
        {
            RoleRuleKind.OnJoin => "on_join",
            RoleRuleKind.SelfSelect => "self_select",
            RoleRuleKind.ExistingRole => "existing_role",
            RoleRuleKind.Tenure => "tenure",
            RoleRuleKind.MinPoints => "min_points",
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
            "points" or "min_points" => RoleRuleKind.MinPoints,
            _ => (RoleRuleKind)(-1)
        };

        return Enum.IsDefined(kind);
    }

    public static bool MatchesTenure(DateTimeOffset joinedAt, DateTimeOffset now, TimeSpan minimum)
        => now - joinedAt >= minimum;

    public static bool MatchesPoints(long balance, long minimum) => balance >= minimum;

    public static bool MatchesExistingRole(IReadOnlySet<long> roleIds, long requiredRoleId)
        => roleIds.Contains(requiredRoleId);
}
