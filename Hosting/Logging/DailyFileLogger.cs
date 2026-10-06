using Microsoft.Extensions.Logging;

namespace Monze.Hosting.Logging;

internal sealed class DailyFileLogger(
    DailyFileLoggerProvider provider,
    string categoryName) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => provider.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel)
        => provider.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        provider.Write(categoryName, logLevel, eventId, state, exception, formatter);
    }
}
