namespace Monze.Application;

public interface IInteractionInboxRepository
{
    Task<InteractionInboxLease?> TryClaimAsync(
        long clanId,
        long channelId,
        long messageId,
        string action,
        CancellationToken cancellationToken);

    Task<bool> CompleteAsync(InteractionInboxLease lease, CancellationToken cancellationToken);

    Task<bool> MarkUncertainAsync(InteractionInboxLease lease, CancellationToken cancellationToken);

    Task ReleaseAsync(InteractionInboxLease lease, CancellationToken cancellationToken);

    Task PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken);
}
