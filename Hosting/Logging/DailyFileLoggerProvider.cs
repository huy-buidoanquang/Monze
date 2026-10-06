using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Monze.Hosting.Logging;

internal sealed class DailyFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, DailyFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private StreamWriter? _writer;
    private DateTime _writerDate;
    private int _disposed;
    private int _writeFailureReported;

    public DailyFileLoggerProvider(DailyFileLoggerOptions options)
    {
        var directory = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(Directory.GetCurrentDirectory(), options.Directory);
        _directory = Path.GetFullPath(directory);
        _minimumLevel = options.MinimumLevel;
        Directory.CreateDirectory(_directory);
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, static (name, provider) => new DailyFileLogger(provider, name), this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        => _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));

    internal IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => _scopeProvider.Push(state);

    internal bool IsEnabled(LogLevel logLevel)
        => Volatile.Read(ref _disposed) == 0
            && logLevel != LogLevel.None
            && logLevel >= _minimumLevel;

    internal void Write<TState>(
        string categoryName,
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

        try
        {
            var now = DateTimeOffset.Now;
            var message = formatter(state, exception);
            var scopes = FormatScopes();

            lock (_sync)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                EnsureWriter(now.Date);
                _writer!.Write(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
                _writer.Write(" [");
                _writer.Write(logLevel.ToString().ToUpperInvariant());
                _writer.Write("] ");
                _writer.Write(categoryName);
                if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
                {
                    _writer.Write(" EventId=");
                    _writer.Write(eventId.Id.ToString(CultureInfo.InvariantCulture));
                    if (!string.IsNullOrWhiteSpace(eventId.Name))
                    {
                        _writer.Write(" (");
                        _writer.Write(eventId.Name);
                        _writer.Write(')');
                    }
                }

                if (scopes.Length > 0)
                {
                    _writer.Write(" Scope=");
                    _writer.Write(scopes);
                }

                _writer.Write(" ");
                _writer.WriteLine(message);
                if (exception is not null)
                {
                    _writer.WriteLine(exception);
                }
            }
        }
        catch (IOException ex)
        {
            ReportWriteFailure(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            ReportWriteFailure(ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private string FormatScopes()
    {
        var scopes = new StringBuilder();
        _scopeProvider.ForEachScope(
            static (scope, builder) =>
            {
                if (builder.Length > 0)
                {
                    builder.Append(" => ");
                }

                builder.Append(scope);
            },
            scopes);
        return scopes.ToString();
    }

    private void EnsureWriter(DateTime date)
    {
        if (_writer is not null && _writerDate == date)
        {
            return;
        }

        _writer?.Dispose();
        var path = Path.Combine(_directory, $"monze-{date:yyyy-MM-dd}.log");
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 4096,
            options: FileOptions.SequentialScan);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        _writerDate = date;
    }

    private void ReportWriteFailure(IOException exception)
    {
        if (Interlocked.Exchange(ref _writeFailureReported, 1) == 0)
        {
            Console.Error.WriteLine($"Monze file logging failed: {exception.Message}");
        }
    }

    private void ReportWriteFailure(UnauthorizedAccessException exception)
    {
        if (Interlocked.Exchange(ref _writeFailureReported, 1) == 0)
        {
            Console.Error.WriteLine($"Monze file logging failed: {exception.Message}");
        }
    }
}
