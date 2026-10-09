namespace Monze.Application;

public interface IAiUsageRepository
{
    Task<(bool Allowed, int Used)> ConsumeAiAsync(long clanId, long userId, int tokens, int dailyCap, CancellationToken cancellationToken);

    /// <summary>
    /// Gives back tokens consumed today for a request the provider did not
    /// answer (never below zero). A refund that crosses midnight lands on the
    /// new day's row, if any.
    /// </summary>
    Task RefundAiAsync(long clanId, long userId, int tokens, CancellationToken cancellationToken);
}
