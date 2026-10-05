namespace Monze;

internal sealed class MessageGapBatch
{
    private readonly Dictionary<ChannelPolicyKey, long> _entries;

    public MessageGapBatch(int capacity)
        => _entries = new Dictionary<ChannelPolicyKey, long>(capacity);

    public int Count => _entries.Count;

    public IReadOnlyDictionary<ChannelPolicyKey, long> Entries => _entries;

    public void Add(long clanId, long channelId, long messageId)
    {
        if (clanId <= 0 || channelId <= 0 || messageId <= 0)
        {
            return;
        }

        var key = new ChannelPolicyKey(clanId, channelId);
        if (!_entries.TryGetValue(key, out var current) || messageId > current)
        {
            _entries[key] = messageId;
        }
    }

    public void Clear() => _entries.Clear();
}
