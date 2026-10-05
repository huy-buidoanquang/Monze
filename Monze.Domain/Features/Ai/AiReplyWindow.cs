namespace Monze.Domain;

public static class AiReplyWindow
{
    public static bool Contains(
        DateTimeOffset? messageCreatedAt,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (messageCreatedAt is not { } createdAt || maximumAge < TimeSpan.Zero)
        {
            return false;
        }

        var age = now - createdAt;
        return age >= TimeSpan.Zero && age <= maximumAge;
    }
}
