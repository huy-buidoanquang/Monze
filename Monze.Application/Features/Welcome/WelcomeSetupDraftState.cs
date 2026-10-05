namespace Monze.Application;

public sealed record WelcomeSetupDraftState(WelcomeSettings Settings, DateTimeOffset ExpiresAt);
