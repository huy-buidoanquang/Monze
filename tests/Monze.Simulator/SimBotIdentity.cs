namespace Monze.Simulator;

/// <summary>
/// The bot application the simulator authenticates: the app id and token the
/// SDK must present at login, and the profile the session JWT carries.
/// </summary>
public sealed record SimBotIdentity(
    long Id,
    string Username,
    string DisplayName,
    string Token);
