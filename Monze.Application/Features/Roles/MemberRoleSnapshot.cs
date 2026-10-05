namespace Monze.Application;

public sealed record MemberRoleSnapshot(
    long UserId,
    bool IsBot,
    DateTimeOffset? JoinedAt,
    IReadOnlySet<long> RoleIds);
