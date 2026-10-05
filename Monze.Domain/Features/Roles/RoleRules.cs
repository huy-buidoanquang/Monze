namespace Monze.Domain;

public static class RoleRules
{
    public static string ToStorageName(RoleRuleKind kind)
        => kind switch
        {
            RoleRuleKind.OnJoin => "on_join",
            RoleRuleKind.Tenure => "tenure",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    public static bool TryParseKind(string value, out RoleRuleKind kind)
    {
        kind = value.Trim().ToLowerInvariant() switch
        {
            "join" or "on_join" => RoleRuleKind.OnJoin,
            "tenure" => RoleRuleKind.Tenure,
            _ => (RoleRuleKind)(-1)
        };

        return Enum.IsDefined(kind);
    }

    public static bool MatchesTenure(DateTimeOffset joinedAt, DateTimeOffset now, TimeSpan minimum)
        => now - joinedAt >= minimum;

}
