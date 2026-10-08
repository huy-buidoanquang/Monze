namespace Monze.Application.Commands;

/// <summary>A window's end as a <see cref="TimeProvider.GetTimestamp"/> value, and its count.</summary>
internal readonly record struct MonzeRateLimitWindow(
    long ExpiresAt,
    int Count);
