namespace Monze;

/// <summary>
/// What survives an Agent SSE reconnect: the id of the last event dispatched
/// (sent back as Last-Event-ID) and the shortest silence after which a
/// stream that sends keepalives is taken for half-open. With a cursor file
/// the id also survives a restart, so events published while no instance
/// was subscribed are replayed (DEF-08).
/// </summary>
internal sealed class AgentSseResumeState
{
    private const int MaxEventIdLength = 256;
    private readonly string? _cursorPath;
    private string? _lastEventId;
    private string? _saved;

    public AgentSseResumeState(TimeSpan idleTimeout, string? cursorPath = null)
    {
        IdleTimeout = idleTimeout;
        _cursorPath = cursorPath;
        if (cursorPath is not null && TryRead(cursorPath) is { } saved)
        {
            _lastEventId = saved;
            _saved = saved;
        }
    }

    public TimeSpan IdleTimeout { get; }

    public string? LastEventId
    {
        get => Volatile.Read(ref _lastEventId);
        set => Volatile.Write(ref _lastEventId, value);
    }

    /// <summary>
    /// Writes the last event id to the cursor file. Call it only once every
    /// event handed to Monze so far has been processed, so a saved id never
    /// skips one. One caller at a time.
    /// </summary>
    public void Save()
    {
        var current = LastEventId;
        if (_cursorPath is null || current is null || current == _saved)
        {
            return;
        }

        var temporary = _cursorPath + ".tmp";
        File.WriteAllText(temporary, current);
        File.Move(temporary, _cursorPath, overwrite: true);
        _saved = current;
    }

    private static string? TryRead(string path)
    {
        try
        {
            var saved = File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;

            // It goes back as a header value: anything that could not have come from an id: field is ignored.
            return saved.Length is > 0 and <= MaxEventIdLength && !saved.Any(char.IsControl) ? saved : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
