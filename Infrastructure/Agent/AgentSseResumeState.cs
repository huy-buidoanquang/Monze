namespace Monze;

/// <summary>
/// What survives an Agent SSE reconnect: the id of the last event dispatched
/// (sent back as Last-Event-ID) and the shortest silence after which a
/// stream that sends keepalives is taken for half-open.
/// </summary>
internal sealed class AgentSseResumeState(TimeSpan idleTimeout)
{
    private string? _lastEventId;

    public TimeSpan IdleTimeout { get; } = idleTimeout;

    public string? LastEventId
    {
        get => Volatile.Read(ref _lastEventId);
        set => Volatile.Write(ref _lastEventId, value);
    }
}
