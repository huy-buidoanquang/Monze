namespace Monze;

internal readonly record struct WelcomeIngressItem(
    long ClanId,
    long UserId,
    bool IsBot,
    string Username,
    string DisplayName);
