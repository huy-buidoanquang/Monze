using System.Net;
using Microsoft.Extensions.Logging;
using Monze.Hosting;
using Monze.Testing;
using Monze.Testing.Harness;
using Npgsql;
using Xunit;

namespace Monze.Tests.Hosting;

/// <summary>
/// Regression for CAND-29: a database that is not reachable when the host
/// starts is retried with a growing delay instead of stopping the host on
/// the first connection error, and the host stops once five minutes are used
/// up. Readiness stays pending while the check is retried.
/// </summary>
public sealed class StartupSchemaValidatorTests
{
    [Fact]
    [Req("REQ-HOST-011")]
    public async Task An_unreachable_database_is_retried_until_the_window_is_used_up()
    {
        // Refuse closes each accepted connection; the target is never contacted.
        await using var proxy = TcpFaultProxy.Start(new IPEndPoint(IPAddress.Loopback, 1));
        proxy.Mode = TcpFaultMode.Refuse;
        await using var dataSource = NpgsqlDataSource.Create(
            $"Host=127.0.0.1;Port={proxy.Endpoint.Port};Database=monze_t_unreachable;Username=monze;Pooling=false");
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var readiness = new StartupReadiness();
        var logger = new WarningLogger();
        var validator = new StartupSchemaValidator(dataSource, readiness, time, logger);

        var start = validator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => logger.Warnings == 1 && time.ActiveTimerCount == 1);
        Assert.False(readiness.Ready.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => logger.Warnings == 2 && time.ActiveTimerCount == 1);
        Assert.False(readiness.Ready.IsCompleted);

        time.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => start.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(readiness.Ready.IsFaulted);
        Assert.Equal(2, logger.Warnings);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the schema check to retry.");
            await Task.Delay(10);
        }
    }

    private sealed class WarningLogger : ILogger<StartupSchemaValidator>
    {
        private int _warnings;

        public int Warnings => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Interlocked.Increment(ref _warnings);
            }
        }
    }
}
