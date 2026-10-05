namespace Monze.Application;

public interface ICommandInboxRepository
{
    Task<CommandInboxLease?> TryClaimAsync(
        long clanId,
        long channelId,
        long messageId,
        CancellationToken cancellationToken);

    Task<bool> CompleteAsync(CommandInboxLease lease, CancellationToken cancellationToken);

    Task<bool> MarkUncertainAsync(CommandInboxLease lease, CancellationToken cancellationToken);

    Task ReleaseAsync(CommandInboxLease lease, CancellationToken cancellationToken);

    Task PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken);
}
