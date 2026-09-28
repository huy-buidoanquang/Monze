namespace Monze.Application.Commands;

internal readonly record struct MonzeRateLimitRule(
    MonzeRateLimitBucket Bucket,
    int Limit,
    TimeSpan Window);
