namespace Monze.Application;

public interface IUserProfileRepository
{
    Task UpsertAsync(
        long clanId,
        long userId,
        string? clanNick,
        string? displayName,
        string? username,
        string? avatarUrl,
        CancellationToken cancellationToken);

    Task<UserProfileSnapshot?> GetByIdAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken);
}
