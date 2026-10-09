using Microsoft.Extensions.Logging;

namespace Monze.Simulator;

/// <summary>One log line written by the Monze host under test.</summary>
public sealed record HostLogEntry(
    DateTimeOffset At,
    LogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    Exception? Exception)
{
    public override string ToString()
        => $"{At:HH:mm:ss.fff} {Level} {Category}: {Message}{(Exception is null ? string.Empty : $" [{Exception.GetType().Name}: {Exception.Message}]")}";
}
