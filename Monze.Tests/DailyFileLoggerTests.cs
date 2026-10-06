using Microsoft.Extensions.Logging;
using Monze.Hosting.Logging;
using Xunit;

namespace Monze.Tests;

public sealed class DailyFileLoggerTests
{
    [Fact]
    public void Writes_debug_entries_to_the_current_daily_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "monze-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            using (var provider = new DailyFileLoggerProvider(
                       new DailyFileLoggerOptions(directory, LogLevel.Debug)))
            {
                var logger = provider.CreateLogger("Monze.Tests.Logging");
                logger.LogDebug("debug message {Value}", 42);
                logger.LogInformation("info message");
            }

            var path = Path.Combine(directory, $"monze-{DateTime.Now:yyyy-MM-dd}.log");
            var contents = File.ReadAllText(path);
            Assert.Contains("[DEBUG] Monze.Tests.Logging", contents, StringComparison.Ordinal);
            Assert.Contains("debug message 42", contents, StringComparison.Ordinal);
            Assert.Contains("[INFORMATION] Monze.Tests.Logging", contents, StringComparison.Ordinal);
            Assert.Contains("info message", contents, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
