namespace Monze.Campaign.Load;

/// <summary>
/// How the simulated platform treats Monze's outbound traffic during a load
/// stage: V1 keeps the client-side transport rate limits Monze ships with
/// (the production ceiling), V3 lifts them so the stage measures Monze
/// itself, and V3L adds a log-normal acknowledgement latency (p50 30 ms,
/// p99 150 ms) to every socket API call and realtime envelope.
/// </summary>
public sealed record LoadVariant(string Id, string Description, bool KeepClientRateLimits, bool AckLatency)
{
    public static readonly LoadVariant V1 = new("V1", "giới hạn transport mặc định của Monze", KeepClientRateLimits: true, AckLatency: false);

    public static readonly LoadVariant V3 = new("V3", "không giới hạn transport (hook test)", KeepClientRateLimits: false, AckLatency: false);

    public static readonly LoadVariant V3L = new("V3L", "V3 + ack log-normal p50 30 ms, p99 150 ms", KeepClientRateLimits: false, AckLatency: true);

    public static IReadOnlyList<LoadVariant> All { get; } = [V1, V3, V3L];
}
