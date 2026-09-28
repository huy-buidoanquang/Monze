using Monze.Domain;

namespace Monze.Application;

public interface IOutboxRepository
{
    Task EnqueueAsync(long clanId, long channelId, OutboxKind kind, string dedupeKey, string body, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListUncertainAsync(long clanId, CancellationToken cancellationToken);
    Task<bool> ResendAsync(long clanId, long outboxId, CancellationToken cancellationToken);
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
