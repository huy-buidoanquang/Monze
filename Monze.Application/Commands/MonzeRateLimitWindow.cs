namespace Monze.Application.Commands;

internal readonly record struct MonzeRateLimitWindow(
    DateTimeOffset ExpiresAt,
    int Count);
