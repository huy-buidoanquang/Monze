namespace Monze.Application;

public sealed record UserProfileSyncState(
    string? ClanNick,
    string? DisplayName,
    string? Username,
    string? AvatarUrl)
{
    public bool Matches(
        string? clanNick,
        string? displayName,
        string? username,
        string? avatarUrl)
        => string.Equals(ClanNick, clanNick, StringComparison.Ordinal)
            && string.Equals(DisplayName, displayName, StringComparison.Ordinal)
            && string.Equals(Username, username, StringComparison.Ordinal)
            && string.Equals(AvatarUrl, avatarUrl, StringComparison.Ordinal);
}
