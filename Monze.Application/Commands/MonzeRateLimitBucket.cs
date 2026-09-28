namespace Monze.Application.Commands;

internal enum MonzeRateLimitBucket : byte
{
    User,
    Ai,
    Meeting,
    Admin
}
