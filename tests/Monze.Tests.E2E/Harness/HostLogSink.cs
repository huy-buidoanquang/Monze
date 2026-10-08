using System.Text;
using Microsoft.Extensions.Logging;

namespace Monze.Tests.E2E.Harness;

/// <summary>
/// Logger provider that keeps every log entry of the host under test in
/// memory, so a test can wait for a lifecycle message and assert that the
/// run logged no errors or exceptions.
/// </summary>
internal sealed class HostLogSink : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<HostLogEntry> _entries = [];
    private readonly List<(Func<HostLogEntry, bool> Predicate, TaskCompletionSource<HostLogEntry> Completion)> _waiters = [];

    public IReadOnlyList<HostLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>Entries at Error or above, and entries carrying an exception.</summary>
    public IReadOnlyList<HostLogEntry> Problems
        => Entries.Where(static entry => entry.Level >= LogLevel.Error || entry.Exception is not null).ToList();

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);

    public async Task<HostLogEntry> WaitForAsync(Func<HostLogEntry, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<HostLogEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(predicate);
            if (existing is not null)
            {
                return existing;
            }

            _waiters.Add((predicate, completion));
        }

        try
        {
            return await completion.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Expected host log entry did not appear within {timeout.TotalSeconds:0.#}s.{Environment.NewLine}{Describe()}");
        }
        finally
        {
            lock (_gate)
            {
                _waiters.RemoveAll(waiter => ReferenceEquals(waiter.Completion, completion));
            }
        }
    }

    public string Describe(int last = 60)
    {
        var builder = new StringBuilder();
        lock (_gate)
        {
            builder.Append("Host log (last ").Append(last).Append(" of ").Append(_entries.Count).AppendLine("):");
            foreach (var entry in _entries.Skip(Math.Max(0, _entries.Count - last)))
            {
                builder.Append("  ").AppendLine(entry.ToString());
            }
        }

        return builder.ToString();
    }

    public void Dispose()
    {
    }

    private void Add(HostLogEntry entry)
    {
        List<TaskCompletionSource<HostLogEntry>>? completed = null;
        lock (_gate)
        {
            _entries.Add(entry);
            foreach (var (predicate, completion) in _waiters)
            {
                if (predicate(entry))
                {
                    (completed ??= []).Add(completion);
                }
            }
        }

        if (completed is not null)
        {
            foreach (var completion in completed)
            {
                completion.TrySetResult(entry);
            }
        }
    }

    private sealed class SinkLogger(HostLogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => sink.Add(new HostLogEntry(DateTimeOffset.UtcNow, logLevel, category, eventId, formatter(state, exception), exception));
    }
}
