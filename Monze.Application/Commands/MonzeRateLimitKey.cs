namespace Monze.Application.Commands;

internal readonly struct MonzeRateLimitKey : IEquatable<MonzeRateLimitKey>
{
    public MonzeRateLimitKey(
        long clanId,
        long userId,
        MonzeRateLimitBucket bucket,
        string command)
    {
        ClanId = clanId;
        UserId = userId;
        Bucket = bucket;
        Command = command;
    }

    public long ClanId { get; }

    public long UserId { get; }

    public MonzeRateLimitBucket Bucket { get; }

    public string Command { get; }

    public bool Equals(MonzeRateLimitKey other)
        => ClanId == other.ClanId
            && UserId == other.UserId
            && Bucket == other.Bucket
            && string.Equals(Command, other.Command, StringComparison.Ordinal);

    public override bool Equals(object? obj)
        => obj is MonzeRateLimitKey other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            ClanId,
            UserId,
            (byte)Bucket,
            StringComparer.Ordinal.GetHashCode(Command));

    public static bool operator ==(MonzeRateLimitKey left, MonzeRateLimitKey right)
        => left.Equals(right);

    public static bool operator !=(MonzeRateLimitKey left, MonzeRateLimitKey right)
        => !left.Equals(right);
}
