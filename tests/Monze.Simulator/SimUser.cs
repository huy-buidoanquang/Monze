namespace Monze.Simulator;

/// <summary>A platform account (human or bot) as Mezon reports it in profiles and member lists.</summary>
public sealed record SimUser(
    long Id,
    string Username,
    string DisplayName,
    bool IsBot,
    string Avatar);
