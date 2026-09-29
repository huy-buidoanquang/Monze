namespace Monze.Application;

public interface IOutboxRepository
{
    Task<IReadOnlyList<DueOutbox>> ClaimDueOutboxAsync(
        CancellationToken cancellationToken,
        long? clanId = null);
    Task CompleteOutboxAsync(
        long id,
        string leaseToken,
        long? externalMessageId,
        bool failed,
        CancellationToken cancellationToken,
        string? errorCode = null);
}
