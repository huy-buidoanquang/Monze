namespace Monze.Application;

public interface IWelcomeDraftStore
{
    WelcomeDraftTicket Create(
        long clanId,
        long channelId,
        bool enabled,
        WelcomeEmbedSettings draft,
        DateTimeOffset now,
        string? text = null,
        long? expectedVersion = null);

    bool TryGet(
        string token,
        long clanId,
        long channelId,
        DateTimeOffset now,
        out WelcomeDraftTicket ticket);

    bool TryClaim(
        string token,
        long clanId,
        long channelId,
        DateTimeOffset now,
        out WelcomeDraftTicket ticket);

    void Complete(WelcomeDraftTicket ticket);

    void Release(WelcomeDraftTicket ticket);
}
