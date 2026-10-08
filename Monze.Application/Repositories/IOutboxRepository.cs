namespace Monze.Application;

public interface IOutboxRepository
{
    /// <summary>Claims up to <paramref name="limit"/> (at most 256) due rows, oldest first.</summary>
    Task<IReadOnlyList<DueOutbox>> ClaimDueOutboxAsync(
        CancellationToken cancellationToken,
        long? clanId = null,
        int limit = 256);

    /// <summary>
    /// Extends the 60 s lease of rows this process is still delivering (id to
    /// lease token), so a send waiting for upstream capacity is never taken
    /// for a lost one. Rows no longer 'sending' under that token are skipped.
    /// </summary>
    Task RenewOutboxLeasesAsync(IReadOnlyDictionary<long, string> leases, CancellationToken cancellationToken);

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
