namespace Monze.Application;

public interface IAuthorizationRepository
{
    Task<bool> IsOwnerAsync(long clanId, long userId, CancellationToken cancellationToken);
    Task<bool> IsAdminAsync(long clanId, long userId, CancellationToken cancellationToken);
    Task<bool> SetDelegateAsync(long clanId, long actorUserId, long userId, bool enabled, CancellationToken cancellationToken);
}
