namespace Monze.Application;

public sealed record WelcomeDraftTicket(
    string Token,
    long ClanId,
    long ChannelId,
    bool Enabled,
    WelcomeEmbedSettings Draft,
    DateTimeOffset ExpiresAt,
    string? Text = null,
    long? ExpectedVersion = null);
