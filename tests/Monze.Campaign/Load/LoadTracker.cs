using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Monze.Simulator;
using Monze.Testing.Harness;

namespace Monze.Campaign.Load;

/// <summary>
/// Correlates what Monze sends on the simulated wire with the load that
/// caused it, from <see cref="SimRecorder.Recorded"/>. A command channel
/// carries one command at a time, so the first message or ephemeral in that
/// channel answers it; the channel is free again 300 ms after its last
/// output (or after 10 s without an answer, which counts as lost). Clicks are
/// matched by the edited message, welcomes by the joined user's id in the
/// welcome channel, outbox rows by their nonce (every nonce is registered
/// before its row exists and forgotten once seen, so an unknown nonce is a
/// duplicate and long runs keep constant memory). Latencies are measured from
/// the scheduled time (so a stalled generator cannot hide queueing) and only
/// for events scheduled inside the measure window.
/// </summary>
public sealed class LoadTracker
{
    private static readonly long QuietTicks = Stopwatch.Frequency * 300 / 1000;
    private static readonly long LostTicks = Stopwatch.Frequency * 10;
    private readonly ConcurrentQueue<(long Clan, long Channel)> _freeChannels = new();
    private readonly ConcurrentDictionary<long, PendingCommand> _commands = new();
    private readonly ConcurrentDictionary<long, Pending> _clicks = new();
    private readonly ConcurrentDictionary<long, Pending> _joins = new();
    private readonly ConcurrentDictionary<string, Pending> _outbox = new(StringComparer.Ordinal);
    private readonly HashSet<long> _welcomeChannels;
    private readonly ConcurrentQueue<(long Clan, long Channel, long Message, long User)> _helpMessages = new();
    private long _commandsSent;
    private long _commandsAnswered;
    private long _commandsLost;
    private long _commandsSkipped;
    private long _extraOutputs;
    private long _clicksSent;
    private long _clicksSkipped;
    private long _joinsSent;
    private long _outboxInserted;
    private long _outboxDuplicates;
    private long _outboundWrites;
    private long _apiReads;
    private long _measureStartTicks = long.MaxValue;
    private long _measureEndTicks = long.MaxValue;

    /// <summary>
    /// Called once per command with its scheduled time and latency, or a null
    /// latency when it was lost; chaos runs build their recovery timeline from it.
    /// </summary>
    public Action<long, TimeSpan?>? CommandCompleted { get; set; }

    public LoadTracker(LoadWorld world)
    {
        foreach (var clan in world.Clans)
        {
            foreach (var channel in clan.CommandChannels)
            {
                _freeChannels.Enqueue((clan.Id, channel));
            }
        }

        _welcomeChannels = world.Clans.Select(static clan => clan.Welcome).ToHashSet();
    }

    public LatencyHistogram Commands { get; } = new();

    public LatencyHistogram Clicks { get; } = new();

    public LatencyHistogram Welcomes { get; } = new();

    public LatencyHistogram Outbox { get; } = new();

    public LatencyHistogram GeneratorLag { get; } = new();

    public long CommandsSent => Interlocked.Read(ref _commandsSent);

    public long CommandsAnswered => Interlocked.Read(ref _commandsAnswered);

    public long CommandsLost => Interlocked.Read(ref _commandsLost);

    public long CommandsSkipped => Interlocked.Read(ref _commandsSkipped);

    public long ExtraOutputs => Interlocked.Read(ref _extraOutputs);

    public long ClicksSent => Interlocked.Read(ref _clicksSent);

    public long ClicksSkipped => Interlocked.Read(ref _clicksSkipped);

    public long JoinsSent => Interlocked.Read(ref _joinsSent);

    public long OutboxInserted => Interlocked.Read(ref _outboxInserted);

    public long OutboxDuplicates => Interlocked.Read(ref _outboxDuplicates);

    /// <summary>Messages, ephemerals, edits and deletes Monze put on the wire inside the measure window.</summary>
    public long OutboundWrites => Interlocked.Read(ref _outboundWrites);

    /// <summary>Read-only socket API calls Monze made inside the measure window.</summary>
    public long ApiReads => Interlocked.Read(ref _apiReads);

    public void SetMeasureWindow(long startTicks, long endTicks)
    {
        Interlocked.Exchange(ref _measureEndTicks, endTicks);
        Interlocked.Exchange(ref _measureStartTicks, startTicks);
    }

    /// <summary>Measured clicks, joins and outbox rows still unanswered (call after the drain).</summary>
    public (long Clicks, long Joins, long Outbox) Unanswered()
        => (_clicks.Values.Count(static pending => pending.Measured),
            _joins.Values.Count(static pending => pending.Measured),
            _outbox.Values.Count(static pending => pending.Measured));

    public bool TryTakeCommandChannel(out (long Clan, long Channel) slot, bool measured)
    {
        if (_freeChannels.TryDequeue(out slot))
        {
            return true;
        }

        if (measured)
        {
            Interlocked.Increment(ref _commandsSkipped);
        }

        return false;
    }

    public void CommandSent(long clan, long channel, long user, string text, long dueTicks, bool measured)
        => _ = CommandSentAsync(clan, channel, user, text, dueTicks, measured);

    /// <summary>Registers a command and completes with its latency when Monze answers it (never if it does not).</summary>
    public Task<TimeSpan> CommandSentAsync(long clan, long channel, long user, string text, long dueTicks, bool measured)
    {
        var pending = new PendingCommand(clan, channel, user, text, dueTicks, measured);
        _commands[channel] = pending;
        if (measured)
        {
            Interlocked.Increment(ref _commandsSent);
        }

        return pending.Answer.Task;
    }

    public bool TryTakeHelpMessage(out (long Clan, long Channel, long Message, long User) target, bool measured)
    {
        if (_helpMessages.TryDequeue(out target))
        {
            return true;
        }

        if (measured)
        {
            Interlocked.Increment(ref _clicksSkipped);
        }

        return false;
    }

    public void ClickSent(long message, long dueTicks, bool measured)
    {
        _clicks[message] = new Pending(dueTicks, measured);
        if (measured)
        {
            Interlocked.Increment(ref _clicksSent);
        }
    }

    public void JoinSent(long user, long dueTicks, bool measured)
    {
        _joins[user] = new Pending(dueTicks, measured);
        if (measured)
        {
            Interlocked.Increment(ref _joinsSent);
        }
    }

    public void OutboxRowInserted(string nonce, long committedTicks, bool measured)
    {
        _outbox[nonce] = new Pending(committedTicks, measured);
        if (measured)
        {
            Interlocked.Increment(ref _outboxInserted);
        }
    }

    /// <summary>Forgets nonces whose insert failed, so they do not count as undelivered.</summary>
    public void OutboxRowsAbandoned(IEnumerable<string> nonces)
    {
        foreach (var nonce in nonces)
        {
            _outbox.TryRemove(nonce, out _);
        }
    }

    /// <summary>Frees quiet command channels and counts commands that got no answer in time.</summary>
    public void Sweep()
    {
        var now = Stopwatch.GetTimestamp();
        foreach (var (channel, pending) in _commands)
        {
            bool release;
            lock (pending)
            {
                release = pending.Answered
                    ? now - pending.LastOutputTicks > QuietTicks
                    : now - pending.DueTicks > LostTicks;
                if (release && !pending.Answered && pending.Measured)
                {
                    Interlocked.Increment(ref _commandsLost);
                    CommandCompleted?.Invoke(pending.DueTicks, null);
                }
            }

            if (release && _commands.TryRemove(new KeyValuePair<long, PendingCommand>(channel, pending)))
            {
                _freeChannels.Enqueue((pending.Clan, channel));
            }
        }
    }

    public void OnRecorded(SimAction action)
    {
        if (!action.IsOutbound)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now >= Interlocked.Read(ref _measureStartTicks) && now < Interlocked.Read(ref _measureEndTicks))
        {
            if (action.Kind is SimActionKind.SendMessage or SimActionKind.SendEphemeral or SimActionKind.UpdateMessage
                or SimActionKind.UpdateEphemeral or SimActionKind.DeleteMessage or SimActionKind.DeleteEphemeral)
            {
                Interlocked.Increment(ref _outboundWrites);
            }
            else if (action.Kind == SimActionKind.ApiRead)
            {
                Interlocked.Increment(ref _apiReads);
            }
        }

        switch (action.Kind)
        {
            case SimActionKind.SendMessage or SimActionKind.SendEphemeral:
                if (_commands.TryGetValue(action.ChannelId, out var command))
                {
                    AnswerCommand(command, action, now);
                }
                else if (_welcomeChannels.Contains(action.ChannelId))
                {
                    AnswerWelcome(action, now);
                }
                else if (action.ContentJson is { } content && content.Contains(LoadStage.OutboxNoncePrefix, StringComparison.Ordinal))
                {
                    AnswerOutbox(content, now);
                }

                break;
            case SimActionKind.UpdateEphemeral:
                if (_clicks.TryRemove(action.MessageId, out var click) && click.Measured)
                {
                    Clicks.Record(Elapsed(click.DueTicks, now));
                }

                break;
        }
    }

    private void AnswerCommand(PendingCommand command, SimAction action, long now)
    {
        lock (command)
        {
            command.LastOutputTicks = now;
            if (command.Answered)
            {
                Interlocked.Increment(ref _extraOutputs);
                return;
            }

            command.Answered = true;
        }

        var latency = Elapsed(command.DueTicks, now);
        if (command.Measured)
        {
            Interlocked.Increment(ref _commandsAnswered);
            Commands.Record(latency);
        }

        command.Answer.TrySetResult(latency);
        if (command.Measured)
        {
            CommandCompleted?.Invoke(command.DueTicks, latency);
        }

        if (action.Kind == SimActionKind.SendEphemeral && command.Text == "*monze help")
        {
            _helpMessages.Enqueue((command.Clan, command.Channel, action.MessageId, command.User));
        }
    }

    private void AnswerWelcome(SimAction action, long now)
    {
        if (action.ContentJson is not { } content)
        {
            return;
        }

        foreach (var (user, pending) in _joins)
        {
            if (content.Contains(user.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                && _joins.TryRemove(user, out _)
                && pending.Measured)
            {
                Welcomes.Record(Elapsed(pending.DueTicks, now));
            }
        }
    }

    private void AnswerOutbox(string content, long now)
    {
        var start = content.IndexOf(LoadStage.OutboxNoncePrefix, StringComparison.Ordinal);
        var end = start + LoadStage.OutboxNoncePrefix.Length;
        while (end < content.Length && char.IsAsciiDigit(content[end]))
        {
            end++;
        }

        if (!_outbox.TryRemove(content[start..end], out var pending))
        {
            Interlocked.Increment(ref _outboxDuplicates);
            return;
        }

        if (pending.Measured)
        {
            Outbox.Record(Elapsed(pending.DueTicks, now));
        }
    }

    private static TimeSpan Elapsed(long fromTicks, long toTicks)
        => TimeSpan.FromTicks((toTicks - fromTicks) * TimeSpan.TicksPerSecond / Stopwatch.Frequency);

    private class Pending(long dueTicks, bool measured)
    {
        public long DueTicks { get; } = dueTicks;

        public bool Measured { get; } = measured;
    }

    private sealed class PendingCommand(long clan, long channel, long user, string text, long dueTicks, bool measured)
        : Pending(dueTicks, measured)
    {
        public long Clan { get; } = clan;

        public long Channel { get; } = channel;

        public long User { get; } = user;

        public string Text { get; } = text;

        public bool Answered { get; set; }

        public long LastOutputTicks { get; set; }

        public TaskCompletionSource<TimeSpan> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
