namespace Monze.Application;

public interface IWelcomeRepository
{
    Task<long> SetWelcomeAsync(long clanId, long actorUserId, bool enabled, string? text, CancellationToken cancellationToken);
    Task<long> SetWelcomeMessageAsync(long clanId, long actorUserId, string text, CancellationToken cancellationToken);
    Task<long> RemoveWelcomeMessageAsync(long clanId, long actorUserId, CancellationToken cancellationToken);
    Task<long> SetWelcomeConfigurationAsync(long clanId, long actorUserId, bool enabled, string? text, WelcomeEmbedSettings embed, CancellationToken cancellationToken);
    Task<long> RemoveWelcomeEmbedAsync(long clanId, long actorUserId, CancellationToken cancellationToken);
    Task<WelcomeSettings?> GetWelcomeAsync(long clanId, CancellationToken cancellationToken);
    Task<bool> TryClaimWelcomeAsync(long clanId, long userId, CancellationToken cancellationToken);
    Task ReleaseWelcomeClaimAsync(long clanId, long userId, CancellationToken cancellationToken);
}
