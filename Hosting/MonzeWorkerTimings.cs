using Microsoft.Extensions.Configuration;

namespace Monze;

/// <summary>
/// Intervals, retry bases and timeouts of the background workers.
/// <see cref="From"/> keeps the production defaults and clamps; tests replace
/// the registered instance (for example with a <c>with</c> expression).
/// </summary>
public sealed record MonzeWorkerTimings(
    TimeSpan SchedulerInterval,
    TimeSpan MaintenanceInterval,
    TimeSpan OutboxPollInterval,
    TimeSpan RoleScanInterval,
    TimeSpan InboxPurgeInterval,
    TimeSpan InboxRetention,
    TimeSpan MessageGapRetryBase,
    TimeSpan AgentScopeRetryBase,
    TimeSpan UncertainMarkTimeout)
{
    public static MonzeWorkerTimings From(IConfiguration configuration)
        => new(
            SchedulerInterval: TimeSpan.FromSeconds(1),
            MaintenanceInterval: TimeSpan.FromMinutes(1),
            OutboxPollInterval: TimeSpan.FromMilliseconds(Math.Clamp(
                configuration.GetValue("Monze:Outbox:PollMilliseconds", 250),
                100,
                5_000)),
            RoleScanInterval: TimeSpan.FromSeconds(Math.Clamp(
                configuration.GetValue("Monze:Roles:ScanIntervalSeconds", 300),
                60,
                3600)),
            InboxPurgeInterval: TimeSpan.FromHours(1),
            InboxRetention: TimeSpan.FromDays(30),
            MessageGapRetryBase: TimeSpan.FromMilliseconds(100),
            AgentScopeRetryBase: TimeSpan.FromMilliseconds(250),
            UncertainMarkTimeout: TimeSpan.FromSeconds(5));
}
