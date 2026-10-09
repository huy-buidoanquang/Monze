namespace Monze.Simulator;

/// <summary>The HTTP dependencies of Monze that <see cref="SimHttpHost"/> fakes.</summary>
public enum SimHttpRoute
{
    /// <summary>GET {Mezon:AgentBaseUrl}/api/sse/metadata?appid=&amp;token= (SDK AgentSseManager).</summary>
    AgentSse,

    /// <summary>POST {Mezon:AgentBaseUrl}/api/v2/auth/mezon/bot/login (HttpTranscriptClient).</summary>
    TranscriptLogin,

    /// <summary>POST {Mezon:AgentBaseUrl}/api/v2/auth/refresh (HttpTranscriptClient).</summary>
    TranscriptRefresh,

    /// <summary>GET {Mezon:AgentBaseUrl}/api/v2/summary/room/id/{roomId} (HttpTranscriptClient).</summary>
    TranscriptSummary,

    /// <summary>POST {Monze:Ai:BaseUrl}/v1/chat/completions (OpenAiCompatibleProvider).</summary>
    AiCompletion
}

public enum SimHttpFaultKind
{
    /// <summary>Answer only after a delay.</summary>
    Delay,

    /// <summary>Answer with an error status code and a small JSON error body.</summary>
    Status,

    /// <summary>Never answer; the request stays open until the client gives up.</summary>
    Hang,

    /// <summary>200 with an empty body (SSE: headers, then the stream ends at once).</summary>
    Empty,

    /// <summary>200 with a body of the given size (SSE: one event of that size, then the stream goes on).</summary>
    Oversized,

    /// <summary>200 with invalid JSON (SSE: garbage frames, then the stream goes on).</summary>
    Malformed,

    /// <summary>200 whose valid body arrives one byte per <see cref="SimHttpFault.Delay"/> (a slow-drip response).</summary>
    Drip
}

/// <summary>A scripted HTTP fault.</summary>
public sealed record SimHttpFault(SimHttpFaultKind Kind, SimHttpRoute Route, TimeSpan Delay, int StatusCode, int Bytes);

/// <summary>
/// Scripted faults of <see cref="SimHttpHost"/>, consumed in the order they
/// were added, per route. Each rule fires <c>times</c> times
/// (<see cref="int.MaxValue"/> for "always"). A delay and one terminal fault
/// (status, hang, empty, oversized, malformed) can apply to the same request.
/// </summary>
public sealed class SimHttpFaultPlan
{
    private readonly object _gate = new();
    private readonly List<Rule> _rules = [];

    public SimHttpFaultPlan Delay(SimHttpRoute route, TimeSpan delay, int times = 1)
        => Add(new SimHttpFault(SimHttpFaultKind.Delay, route, delay, 0, 0), times);

    /// <summary>Answer with <paramref name="statusCode"/> (400 to 599), e.g. 401, 429, 500, 503.</summary>
    public SimHttpFaultPlan Status(SimHttpRoute route, int statusCode, int times = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(statusCode, 400);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(statusCode, 599);
        return Add(new SimHttpFault(SimHttpFaultKind.Status, route, TimeSpan.Zero, statusCode, 0), times);
    }

    public SimHttpFaultPlan Hang(SimHttpRoute route, int times = 1)
        => Add(new SimHttpFault(SimHttpFaultKind.Hang, route, TimeSpan.Zero, 0, 0), times);

    public SimHttpFaultPlan Empty(SimHttpRoute route, int times = 1)
        => Add(new SimHttpFault(SimHttpFaultKind.Empty, route, TimeSpan.Zero, 200, 0), times);

    public SimHttpFaultPlan Oversized(SimHttpRoute route, int bytes, int times = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, 64 * 1024 * 1024);
        return Add(new SimHttpFault(SimHttpFaultKind.Oversized, route, TimeSpan.Zero, 200, bytes), times);
    }

    public SimHttpFaultPlan Malformed(SimHttpRoute route, int times = 1)
        => Add(new SimHttpFault(SimHttpFaultKind.Malformed, route, TimeSpan.Zero, 200, 0), times);

    /// <summary>Answer normally, but write the body one byte every <paramref name="perByte"/> (headers go out at once).</summary>
    public SimHttpFaultPlan Drip(SimHttpRoute route, TimeSpan perByte, int times = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(perByte, TimeSpan.Zero);
        return Add(new SimHttpFault(SimHttpFaultKind.Drip, route, perByte, 200, 0), times);
    }

    /// <summary>Removes every pending rule.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _rules.Clear();
        }
    }

    /// <summary>Rules that have not fired their full count yet.</summary>
    public IReadOnlyList<SimHttpFault> Pending
    {
        get
        {
            lock (_gate)
            {
                return _rules.Select(static rule => rule.Fault).ToList();
            }
        }
    }

    /// <summary>Takes at most one delay and one terminal fault for a request.</summary>
    internal (SimHttpFault? Delay, SimHttpFault? Terminal) Take(SimHttpRoute route)
    {
        lock (_gate)
        {
            return (
                TakeLocked(route, static kind => kind == SimHttpFaultKind.Delay),
                TakeLocked(route, static kind => kind != SimHttpFaultKind.Delay));
        }
    }

    private SimHttpFault? TakeLocked(SimHttpRoute route, Func<SimHttpFaultKind, bool> kinds)
    {
        for (var i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];
            if (rule.Fault.Route != route || !kinds(rule.Fault.Kind))
            {
                continue;
            }

            rule.Remaining--;
            if (rule.Remaining <= 0)
            {
                _rules.RemoveAt(i);
            }

            return rule.Fault;
        }

        return null;
    }

    private SimHttpFaultPlan Add(SimHttpFault fault, int times)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);
        lock (_gate)
        {
            _rules.Add(new Rule(fault, times));
        }

        return this;
    }

    private sealed class Rule(SimHttpFault fault, int remaining)
    {
        public SimHttpFault Fault { get; } = fault;

        public int Remaining { get; set; } = remaining;
    }
}

/// <summary>
/// One request the fake HTTP host answered. <see cref="Path"/> never carries
/// the query string (the SSE query holds the bot token); <see cref="Route"/>
/// is null for an unmodelled path.
/// </summary>
public sealed record SimHttpRequest(
    long Sequence,
    DateTimeOffset At,
    SimHttpRoute? Route,
    string Method,
    string Path,
    int Status,
    SimHttpFaultKind? Fault,
    string? Detail = null,
    bool HadLastEventId = false);

/// <summary>A chat completion request as OpenAiCompatibleProvider sent it.</summary>
public sealed record SimAiRequest(string? Model, string? Instruction, string? Input, int? MaxTokens);

/// <summary>How <see cref="SimHttpHost.PublishAgentEventAsync"/> delivers an Agent event.</summary>
public enum SimSseDelivery
{
    Normal,

    /// <summary>Write the frame twice.</summary>
    Duplicate,

    /// <summary>Hold the frame and write it right after the next published frame (reordering).</summary>
    Hold
}
