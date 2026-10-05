using Monze.Application;

namespace Monze.Tests;

internal sealed class StubMeetingUserProfileRepository : IUserProfileRepository
{
    private readonly Func<long, long, UserProfileSnapshot?> _resolve;

    public StubMeetingUserProfileRepository()
        : this(static (clanId, userId) => new UserProfileSnapshot(
            clanId,
            userId,
            "Clan Nick",
            "Display",
            "username",
            null,
            DateTimeOffset.UtcNow))
    {
    }

    public StubMeetingUserProfileRepository(Func<long, long, UserProfileSnapshot?> resolve)
        => _resolve = resolve;

    public Task<UserProfileSnapshot?> GetByIdAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
        => Task.FromResult(_resolve(clanId, userId));

    public Task UpsertAsync(
        long clanId,
        long userId,
        string? clanNick,
        string? displayName,
        string? username,
        string? avatarUrl,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
