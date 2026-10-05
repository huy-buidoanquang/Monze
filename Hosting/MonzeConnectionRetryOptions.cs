using Microsoft.Extensions.Configuration;

namespace Monze;

public sealed record MonzeConnectionRetryOptions(TimeSpan InitialDelay, TimeSpan MaxDelay)
{
    public static MonzeConnectionRetryOptions From(IConfiguration configuration)
    {
        var initialSeconds = Math.Clamp(
            configuration.GetValue("Monze:Connection:InitialRetrySeconds", 1),
            1,
            60);
        var maxSeconds = Math.Clamp(
            configuration.GetValue("Monze:Connection:MaxRetrySeconds", 30),
            initialSeconds,
            300);
        return new MonzeConnectionRetryOptions(
            TimeSpan.FromSeconds(initialSeconds),
            TimeSpan.FromSeconds(maxSeconds));
    }
}
