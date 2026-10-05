using Monze.Application.Commands;

namespace Monze.Application;

public sealed partial class MonzeApp
{
    public async Task<CommandOutcome> SetWelcomeAsync(
        long clanId,
        long userId,
        bool enabled,
        string? text,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.WelcomeAdminOnly, tone: MonzeTone.Error);
        }

        var version = await _welcome.SetWelcomeAsync(clanId, userId, enabled, text, cancellationToken);
        if (version == 0)
        {
            return Say(MonzeMessages.WelcomeAdminOnly, tone: MonzeTone.Error);
        }

        try
        {
            await _readModelCache.InvalidateAsync(clanId, "welcome", "settings", version, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Cache invalidation is best effort; PostgreSQL remains authoritative.
        }

        return new CommandOutcome
        {
            Title = MonzeMessages.TitleWelcome,
            Text = enabled ? MonzeMessages.WelcomeEnabled : MonzeMessages.WelcomeDisabled,
            ShowWelcomeHelp = true
        };
    }
}
