namespace Monze.Application;

public interface IWelcomeSetupDraftStore
{
    bool TryGet(WelcomeSetupDraftKey key, DateTimeOffset now, out WelcomeSettings settings);
    void Set(WelcomeSetupDraftKey key, WelcomeSettings settings, DateTimeOffset now);
    void Remove(WelcomeSetupDraftKey key);
}
