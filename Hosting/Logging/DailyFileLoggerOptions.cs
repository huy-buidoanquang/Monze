using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Monze.Hosting.Logging;

internal sealed class DailyFileLoggerOptions
{
    public DailyFileLoggerOptions(string directory, LogLevel minimumLevel)
    {
        Directory = directory;
        MinimumLevel = minimumLevel;
    }

    public string Directory { get; }

    public LogLevel MinimumLevel { get; }

    public static DailyFileLoggerOptions From(IConfiguration configuration)
    {
        var directory = configuration["Monze:Logging:Directory"];
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = "logs";
        }

        var minimumLevel = LogLevel.Debug;
        var configuredLevel = configuration["Monze:Logging:MinimumLevel"];
        if (!string.IsNullOrWhiteSpace(configuredLevel)
            && Enum.TryParse(configuredLevel, ignoreCase: true, out LogLevel parsedLevel))
        {
            minimumLevel = parsedLevel;
        }

        return new DailyFileLoggerOptions(directory, minimumLevel);
    }
}
