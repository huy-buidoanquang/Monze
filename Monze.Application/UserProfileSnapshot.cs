namespace Monze.Application;

public sealed record UserProfileSnapshot(
    long ClanId,
    long UserId,
    string? ClanNick,
    string? DisplayName,
    string? Username,
    string? AvatarUrl,
    DateTimeOffset UpdatedAt)
{
    public string Label
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ClanNick))
            {
                return ClanNick.Trim();
            }

            if (!string.IsNullOrWhiteSpace(DisplayName))
            {
                return DisplayName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(Username))
            {
                return Username.Trim();
            }

            return UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
