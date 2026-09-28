namespace Monze.Application;

public sealed record WelcomeSettings(
    bool Enabled,
    string? Text,
    long Version,
    WelcomeEmbedSettings? Embed = null);
