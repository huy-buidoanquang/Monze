namespace Monze.Testing.Twin;

// Mirrors Monze.Infrastructure/Persistence/PostgresAuthorizationRepository.cs.
public sealed partial class InMemoryMonzeState
{
    public Task<bool> IsOwnerAsync(long clanId, long userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(IsOwnerLocked(clanId, userId));
        }
    }

    public Task<bool> IsAdminAsync(long clanId, long userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(IsAdminLocked(clanId, userId));
        }
    }

    public Task<bool> SetDelegateAsync(
        long clanId,
        long actorUserId,
        long userId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // Only the owner of an active clan writes; the result is true
            // only when a row was inserted (ON CONFLICT DO NOTHING) or deleted.
            if (!IsOwnerLocked(clanId, actorUserId))
            {
                return Task.FromResult(false);
            }

            var changed = enabled
                ? _admins.Add((clanId, userId))
                : _admins.Remove((clanId, userId));
            return Task.FromResult(changed);
        }
    }
}
