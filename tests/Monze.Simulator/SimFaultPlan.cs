using Mezon.Net.Core;

namespace Monze.Simulator;

/// <summary>
/// Scripted faults of the simulated platform, consumed in the order they were
/// added. Operation faults are keyed by <see cref="SimOperations"/> names
/// (socket API name, realtime envelope name, REST login or socket connect);
/// push faults by <see cref="SimPushKind"/>. Each rule fires
/// <c>times</c> times (use <see cref="int.MaxValue"/> for "always").
/// A delay and one terminal fault (error, dropped response, socket close) can
/// apply to the same call.
/// </summary>
public sealed class SimFaultPlan
{
    private readonly object _gate = new();
    private readonly List<Rule> _rules = [];

    /// <summary>Handle the operation only after <paramref name="delay"/>.</summary>
    public SimFaultPlan Delay(string operation, TimeSpan delay, int times = 1)
        => Add(SimFaultKind.Delay, operation, times, delay: delay);

    /// <summary>Answer with <paramref name="code"/> and change nothing.</summary>
    public SimFaultPlan Fail(string operation, MezonStatusCode code, int times = 1, string? detail = null)
        => Add(SimFaultKind.Error, operation, times, code: code, detail: detail);

    /// <summary>Apply the operation but never answer it (the SDK times out).</summary>
    public SimFaultPlan DropResponse(string operation, int times = 1)
        => Add(SimFaultKind.DropResponse, operation, times);

    /// <summary>Close the socket when the operation arrives (drives SDK reconnect).</summary>
    public SimFaultPlan CloseSocket(string operation, int times = 1)
        => Add(SimFaultKind.CloseSocket, operation, times);

    /// <summary>Refuse the next socket handshakes.</summary>
    public SimFaultPlan RefuseConnect(int times = 1)
        => Add(SimFaultKind.RefuseConnect, SimOperations.SocketConnect, times);

    /// <summary>Deliver the next pushes of <paramref name="kind"/> twice.</summary>
    public SimFaultPlan DuplicatePush(SimPushKind kind, int times = 1)
        => Add(SimFaultKind.DuplicatePush, kind.ToString(), times);

    /// <summary>Hold the next push of <paramref name="kind"/> and deliver it after the following push.</summary>
    public SimFaultPlan ReorderPush(SimPushKind kind, int times = 1)
        => Add(SimFaultKind.ReorderPush, kind.ToString(), times);

    /// <summary>Never deliver the next pushes of <paramref name="kind"/>.</summary>
    public SimFaultPlan DropPush(SimPushKind kind, int times = 1)
        => Add(SimFaultKind.DropPush, kind.ToString(), times);

    /// <summary>Removes every pending rule.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _rules.Clear();
        }
    }

    /// <summary>Rules that have not fired their full count yet.</summary>
    public IReadOnlyList<SimFault> Pending
    {
        get
        {
            lock (_gate)
            {
                return _rules.Select(static rule => rule.Fault).ToList();
            }
        }
    }

    /// <summary>Takes at most one delay and one terminal fault for an operation.</summary>
    internal (SimFault? Delay, SimFault? Terminal) TakeOperation(string operation)
    {
        lock (_gate)
        {
            return (
                TakeLocked(operation, static kind => kind == SimFaultKind.Delay),
                TakeLocked(operation, static kind => kind is SimFaultKind.Error or SimFaultKind.DropResponse or SimFaultKind.CloseSocket or SimFaultKind.RefuseConnect));
        }
    }

    /// <summary>Takes at most one push fault for a push kind.</summary>
    internal SimFault? TakePush(SimPushKind kind)
    {
        lock (_gate)
        {
            return TakeLocked(kind.ToString(), static faultKind => faultKind is SimFaultKind.DuplicatePush or SimFaultKind.ReorderPush or SimFaultKind.DropPush);
        }
    }

    private SimFault? TakeLocked(string target, Func<SimFaultKind, bool> kinds)
    {
        for (var i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];
            if (!string.Equals(rule.Fault.Target, target, StringComparison.Ordinal) || !kinds(rule.Fault.Kind))
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

    private SimFaultPlan Add(
        SimFaultKind kind,
        string target,
        int times,
        TimeSpan delay = default,
        MezonStatusCode code = MezonStatusCode.Ok,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);
        if (kind == SimFaultKind.Error && code == MezonStatusCode.Ok)
        {
            throw new ArgumentException("An error fault needs a non-OK status code.", nameof(code));
        }

        lock (_gate)
        {
            _rules.Add(new Rule(new SimFault(kind, target, delay, code, detail), times));
        }

        return this;
    }

    private sealed class Rule(SimFault fault, int remaining)
    {
        public SimFault Fault { get; } = fault;

        public int Remaining { get; set; } = remaining;
    }
}
