using Monze.Domain;

namespace Monze.Application;

public interface IAuthorizationRepository
{
    Task<bool> IsOwnerAsync(long clanId, long userId, CancellationToken cancellationToken);
    Task<bool> IsAdminAsync(long clanId, long userId, CancellationToken cancellationToken);
    Task<bool> SetDelegateAsync(long clanId, long actorUserId, long userId, bool enabled, CancellationToken cancellationToken);
    Task<long> SetWelcomeAsync(long clanId, long actorUserId, bool enabled, string? text, CancellationToken cancellationToken);
    Task<long> SetWelcomeMessageAsync(long clanId, long actorUserId, string text, CancellationToken cancellationToken);
    Task<long> RemoveWelcomeMessageAsync(long clanId, long actorUserId, CancellationToken cancellationToken);
    Task<long> SetWelcomeEmbedAsync(long clanId, long actorUserId, WelcomeEmbedSettings embed, CancellationToken cancellationToken);
    Task<long> SetWelcomeConfigurationAsync(long clanId, long actorUserId, bool enabled, string? text, WelcomeEmbedSettings embed, CancellationToken cancellationToken);
    Task<long> RemoveWelcomeEmbedAsync(long clanId, long actorUserId, CancellationToken cancellationToken);
    Task<WelcomeSettings?> GetWelcomeAsync(long clanId, CancellationToken cancellationToken);
    Task<bool> TryClaimWelcomeAsync(long clanId, long userId, CancellationToken cancellationToken);
    Task ReleaseWelcomeClaimAsync(long clanId, long userId, CancellationToken cancellationToken);
    Task<bool> IsRoleSelfAssignableAsync(long clanId, long roleId, CancellationToken cancellationToken);
    Task<bool> SetRoleSelfAssignableAsync(long clanId, long actorUserId, long roleId, bool enabled, CancellationToken cancellationToken);
    Task<bool> IsRoleAutomationEnabledAsync(long clanId, CancellationToken cancellationToken);
    Task<bool> SetRoleAutomationEnabledAsync(long clanId, long actorUserId, bool enabled, CancellationToken cancellationToken);
    Task<IReadOnlyList<AutoRoleRule>> ListEnabledRoleRulesAsync(long clanId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AutoRoleRule>> ListEnabledRoleRulesAsync(CancellationToken cancellationToken);
    Task<bool> SetRoleRuleAsync(long clanId, long actorUserId, long roleId, RoleRuleKind kind, string? conditionValue, CancellationToken cancellationToken);
    Task<bool> RemoveRoleRuleAsync(long clanId, long actorUserId, long roleId, RoleRuleKind kind, CancellationToken cancellationToken);
    Task RecordRoleGrantAsync(long clanId, long roleId, long userId, CancellationToken cancellationToken);
}
