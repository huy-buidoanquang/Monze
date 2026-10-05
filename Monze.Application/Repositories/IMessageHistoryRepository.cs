namespace Monze.Application;

public interface IMessageHistoryRepository
{
    Task<bool> ChannelPersistsAsync(long clanId, long channelId, CancellationToken cancellationToken);
    Task MarkChannelGapAsync(long clanId, long channelId, long messageId, CancellationToken cancellationToken);
    Task<bool> ChannelHasGapAsync(long clanId, long channelId, CancellationToken cancellationToken);
}
