using Monze.Domain;

namespace Monze.Application;

public interface ICommunityRepository
{
    Task<long> CreateEventAsync(long clanId, long channelId, long ownerId, string title, DateTimeOffset startsAt, int? capacity, CancellationToken cancellationToken);
    Task<EventJoinStatus> JoinEventAsync(long clanId, long userId, CancellationToken cancellationToken);
    Task<string?> RecapEventAsync(long clanId, CancellationToken cancellationToken);
    Task AddFaqAsync(long clanId, string question, string answer, CancellationToken cancellationToken);
    Task<string?> FindFaqAsync(long clanId, string query, CancellationToken cancellationToken);
    Task<(bool Applied, long Balance)> AddPointsAsync(long clanId, long userId, long delta, string sourceType, string sourceId, int dailyCap, CancellationToken cancellationToken);
    Task<IReadOnlyList<long>> UsersWithMinimumPointsAsync(long clanId, long minimum, CancellationToken cancellationToken);
    Task<IReadOnlyList<(long UserId, long Points)>> LeaderboardAsync(long clanId, int limit, CancellationToken cancellationToken);
    Task<int?> SpinAsync(long clanId, long userId, IReadOnlyList<int> weights, int roll, CancellationToken cancellationToken);
    Task<string?> NextTopicAsync(long clanId, CancellationToken cancellationToken);
    Task<bool> AddTopicAsync(long clanId, string text, CancellationToken cancellationToken);
    Task<bool> RemoveTopicAsync(long clanId, string text, CancellationToken cancellationToken);
}
