namespace Monze.Application;

public interface IMessageHistoryRepository
{
    Task<bool> ChannelPersistsAsync(long clanId, long channelId, CancellationToken cancellationToken);
    Task NoteMessageAsync(long clanId, long channelId, long messageId, bool gap, CancellationToken cancellationToken);
    Task<bool> ChannelHasGapAsync(long clanId, long channelId, CancellationToken cancellationToken);
}
