namespace Monze.Application;

public interface IAiUsageRepository
{
    Task<(bool Allowed, int Used)> ConsumeAiAsync(long clanId, long userId, int tokens, int dailyCap, CancellationToken cancellationToken);
}
