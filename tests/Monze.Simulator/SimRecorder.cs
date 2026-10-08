using System.Text;

namespace Monze.Simulator;

/// <summary>
/// Ordered, thread-safe log of everything the bot did on the simulated wire
/// (messages with content JSON, replies, mentions, ephemeral receivers,
/// updates, deletes, role changes, clan joins, API reads), interleaved with
/// the platform pushes it received. Tests await expected actions with
/// <see cref="WaitForAsync"/> and assert <see cref="UnmodelledCalls"/> and
/// <see cref="ProtocolViolations"/> are empty.
/// </summary>
public sealed class SimRecorder
{
    private readonly object _gate = new();
    private readonly List<SimAction> _actions = [];
    private readonly List<SimUnmodelledCall> _unmodelled = [];
    private readonly List<SimProtocolViolation> _violations = [];
    private readonly List<Waiter> _waiters = [];
    private readonly TimeProvider _time;
    private long _sequence;
    private long _lastOutboundTicks;

    public SimRecorder(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _lastOutboundTicks = _time.GetTimestamp();
    }

    /// <summary>Snapshot of the whole log.</summary>
    public IReadOnlyList<SimAction> Actions
    {
        get
        {
            lock (_gate)
            {
                return _actions.ToList();
            }
        }
    }

    public IReadOnlyList<SimUnmodelledCall> UnmodelledCalls
    {
        get
        {
            lock (_gate)
            {
                return _unmodelled.ToList();
            }
        }
    }

    public IReadOnlyList<SimProtocolViolation> ProtocolViolations
    {
        get
        {
            lock (_gate)
            {
                return _violations.ToList();
            }
        }
    }

    /// <summary>Sequence of the last entry; pass it as <c>afterSequence</c> to only see later actions.</summary>
    public long LastSequence => Interlocked.Read(ref _sequence);

    /// <summary>Distinct wire operations of outbound actions.</summary>
    public IReadOnlySet<string> ObservedOperations
    {
        get
        {
            lock (_gate)
            {
                return _actions.Where(static action => action.IsOutbound)
                    .Select(static action => action.Operation)
                    .ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Entries recorded after <paramref name="sequence"/>.</summary>
    public IReadOnlyList<SimAction> Since(long sequence)
    {
        lock (_gate)
        {
            return _actions.Where(action => action.Sequence > sequence).ToList();
        }
    }

    /// <summary>Waits for the first entry after <paramref name="afterSequence"/> that matches.</summary>
    public async Task<SimAction> WaitForAsync(
        Func<SimAction, bool> predicate,
        TimeSpan timeout,
        long afterSequence = 0,
        CancellationToken cancellationToken = default)
    {
        var matches = await WaitForCountAsync(predicate, 1, timeout, afterSequence, cancellationToken).ConfigureAwait(false);
        return matches[0];
    }

    /// <summary>Waits until <paramref name="count"/> matching entries exist after <paramref name="afterSequence"/>.</summary>
    public async Task<IReadOnlyList<SimAction>> WaitForCountAsync(
        Func<SimAction, bool> predicate,
        int count,
        TimeSpan timeout,
        long afterSequence = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        Waiter waiter;
        lock (_gate)
        {
            var existing = _actions.Where(action => action.Sequence > afterSequence && predicate(action)).Take(count).ToList();
            if (existing.Count == count)
            {
                return existing;
            }

            waiter = new Waiter(predicate, count, afterSequence, existing);
            _waiters.Add(waiter);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await waiter.Completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No {count} matching simulator action(s) within {timeout.TotalSeconds:0.#}s after #{afterSequence}.{Environment.NewLine}{Describe()}");
        }
        finally
        {
            lock (_gate)
            {
                _waiters.Remove(waiter);
            }
        }
    }

    /// <summary>
    /// Waits until no outbound action other than a heartbeat was recorded
    /// for <paramref name="quiet"/>, or <paramref name="max"/> elapsed.
    /// </summary>
    public async Task WaitForQuietAsync(TimeSpan quiet, TimeSpan max, CancellationToken cancellationToken = default)
    {
        var deadline = _time.GetTimestamp() + (long)(max.TotalSeconds * _time.TimestampFrequency);
        while (true)
        {
            var sinceLast = _time.GetElapsedTime(Interlocked.Read(ref _lastOutboundTicks));
            if (sinceLast >= quiet || _time.GetTimestamp() >= deadline)
            {
                return;
            }

            await Task.Delay(quiet - sinceLast, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Human-readable dump of the last entries for failure messages.</summary>
    public string Describe(int last = 40)
    {
        var builder = new StringBuilder();
        lock (_gate)
        {
            builder.Append("Simulator log (last ").Append(last).Append(" of ").Append(_actions.Count).AppendLine("):");
            foreach (var action in _actions.Skip(Math.Max(0, _actions.Count - last)))
            {
                builder.Append("  ").AppendLine(action.ToString());
            }

            foreach (var call in _unmodelled)
            {
                builder.Append("  UNMODELLED ").Append(call.Operation).Append(": ").AppendLine(call.Detail);
            }

            foreach (var violation in _violations)
            {
                builder.Append("  VIOLATION ").Append(violation.Operation).Append(": ").AppendLine(violation.Detail);
            }
        }

        return builder.ToString();
    }

    internal SimAction Record(SimAction draft)
    {
        List<(Waiter Waiter, IReadOnlyList<SimAction> Result)>? completed = null;
        SimAction action;
        lock (_gate)
        {
            action = draft with { Sequence = Interlocked.Increment(ref _sequence), At = _time.GetUtcNow() };
            _actions.Add(action);
            if (action.IsOutbound && action.Kind != SimActionKind.Heartbeat)
            {
                Interlocked.Exchange(ref _lastOutboundTicks, _time.GetTimestamp());
            }

            foreach (var waiter in _waiters)
            {
                if (action.Sequence > waiter.AfterSequence && waiter.Predicate(action))
                {
                    waiter.Matches.Add(action);
                    if (waiter.Matches.Count == waiter.Count)
                    {
                        (completed ??= []).Add((waiter, waiter.Matches.ToList()));
                    }
                }
            }

            if (completed is not null)
            {
                foreach (var (waiter, _) in completed)
                {
                    _waiters.Remove(waiter);
                }
            }
        }

        if (completed is not null)
        {
            foreach (var (waiter, result) in completed)
            {
                waiter.Completion.TrySetResult(result);
            }
        }

        return action;
    }

    internal void RecordUnmodelled(int sessionId, string operation, string detail)
    {
        lock (_gate)
        {
            _unmodelled.Add(new SimUnmodelledCall(_time.GetUtcNow(), sessionId, operation, detail));
        }
    }

    internal void RecordViolation(int sessionId, string operation, string detail)
    {
        lock (_gate)
        {
            _violations.Add(new SimProtocolViolation(_time.GetUtcNow(), sessionId, operation, detail));
        }
    }

    private sealed class Waiter(Func<SimAction, bool> predicate, int count, long afterSequence, List<SimAction> matches)
    {
        public Func<SimAction, bool> Predicate { get; } = predicate;

        public int Count { get; } = count;

        public long AfterSequence { get; } = afterSequence;

        public List<SimAction> Matches { get; } = matches;

        public TaskCompletionSource<IReadOnlyList<SimAction>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
