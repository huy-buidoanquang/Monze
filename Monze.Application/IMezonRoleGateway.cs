namespace Monze.Application;

public interface IMezonRoleGateway
{
    Task<IReadOnlyList<MemberRoleSnapshot>> ListMembersAsync(
        long clanId,
        CancellationToken cancellationToken);

    Task<RoleResolutionResult> ResolveRoleAsync(
        long clanId,
        string roleSelector,
        CancellationToken cancellationToken);

    Task<RoleAssignmentResult> AddUserToRoleAsync(
        long clanId,
        long roleId,
        long userId,
        CancellationToken cancellationToken);
}
