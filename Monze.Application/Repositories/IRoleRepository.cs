using Monze.Domain;

namespace Monze.Application;

public interface IRoleRepository
{
    Task<bool> IsRoleAutomationEnabledAsync(long clanId, CancellationToken cancellationToken);
    Task<bool> SetRoleAutomationEnabledAsync(long clanId, long actorUserId, bool enabled, CancellationToken cancellationToken);
    Task<IReadOnlyList<AutoRoleRule>> ListEnabledRoleRulesAsync(long clanId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AutoRoleRule>> ListEnabledRoleRulesAsync(CancellationToken cancellationToken);
    Task<bool> SetRoleRuleAsync(long clanId, long actorUserId, long roleId, RoleRuleKind kind, string? conditionValue, CancellationToken cancellationToken);
    Task<bool> RemoveRoleRuleAsync(long clanId, long actorUserId, long roleId, RoleRuleKind kind, CancellationToken cancellationToken);
    Task RecordRoleGrantAsync(long clanId, long roleId, long userId, CancellationToken cancellationToken);
}
