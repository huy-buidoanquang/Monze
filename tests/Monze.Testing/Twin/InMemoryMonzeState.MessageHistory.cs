namespace Monze.Testing.Twin;

// Mirrors Monze.Infrastructure/Persistence/PostgresMessageHistoryRepository.cs.
public sealed partial class InMemoryMonzeState
{
    // channel_policy (clan_id, channel_id). No port creates rows; seed them.
    private readonly Dictionary<(long ClanId, long ChannelId), ChannelPolicyRow> _channelPolicies = new();

    /// <summary>Seeds or replaces a channel_policy row.</summary>
    public void AddChannelPolicy(
        long clanId,
        long channelId,
        bool persistMessages,
        bool hasGap = false,
        long? lastMessageId = null)
    {
        lock (_gate)
        {
            _channelPolicies[(clanId, channelId)] = new ChannelPolicyRow
            {
                PersistMessages = persistMessages,
                HasGap = hasGap,
                LastMessageId = lastMessageId
            };
        }
    }

    public Task<bool> ChannelPersistsAsync(long clanId, long channelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(
                _channelPolicies.TryGetValue((clanId, channelId), out var row) && row.PersistMessages);
        }
    }

    public Task MarkChannelGapAsync(
        long clanId,
        long channelId,
        long messageId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // UPDATE only, and only for channels that persist messages.
            if (_channelPolicies.TryGetValue((clanId, channelId), out var row) && row.PersistMessages)
            {
                row.LastMessageId = Math.Max(row.LastMessageId ?? messageId, messageId);
                row.HasGap = true;
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> ChannelHasGapAsync(long clanId, long channelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(
                _channelPolicies.TryGetValue((clanId, channelId), out var row) && row.HasGap);
        }
    }

    private sealed class ChannelPolicyRow
    {
        public bool PersistMessages { get; set; }

        public long? LastMessageId { get; set; }

        public bool HasGap { get; set; }
    }
}
