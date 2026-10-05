namespace Monze.Application;

public sealed record RoleResolutionResult(bool Found, long RoleId, string Label);
