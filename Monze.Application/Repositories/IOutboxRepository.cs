namespace Monze.Application;

public interface IOutboxRepository
{
    Task<IReadOnlyList<DueOutbox>> ClaimDueOutboxAsync(
        CancellationToken cancellationToken,
        long? clanId = null);
    /// <summary>
    /// Ends a delivery attempt: sent with <paramref name="externalMessageId"/>,
    /// held ('uncertain') when <paramref name="failed"/>, otherwise back to
    /// 'pending' with a backoff. <paramref name="countsAsAttempt"/> = false is
    /// for a send that provably never left the process (the socket was
    /// closed): it is retried without using up the row's attempts.
    /// </summary>
    Task CompleteOutboxAsync(
        long id,
        string leaseToken,
        long? externalMessageId,
        bool failed,
        CancellationToken cancellationToken,
        string? errorCode = null,
        bool countsAsAttempt = true);

    /// <summary>
    /// Leases rows whose delivery is uncertain (an ack that never came, or a
    /// 'sending' lease that expired, for example after a crash or a lost
    /// database connection) so they can be checked against the channel before
    /// any resend. Rows waiting on a parent that is held for an administrator
    /// are held too.
    /// </summary>
    Task<IReadOnlyList<UncertainOutbox>> ClaimUncertainOutboxAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a leased uncertain row whose message is not on the channel to
    /// 'pending' (resend now), or holds it once it has used up its attempts.
    /// </summary>
    Task RequeueUncertainOutboxAsync(long id, string leaseToken, CancellationToken cancellationToken);

    /// <summary>Which of <paramref name="messageIds"/> are already recorded as a delivered outbox message.</summary>
    Task<IReadOnlySet<long>> FindRecordedMessagesAsync(IReadOnlyList<long> messageIds, CancellationToken cancellationToken);
}
