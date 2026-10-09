using System.Text.Json;
using Monze.Application;

namespace Monze.Testing.Twin;

// Mirrors Monze.Infrastructure/Persistence/PostgresWelcomeRepository.cs.
// Every writer returns the new clan_settings.version, or 0 when no row was
// written (actor not an admin of an active clan, missing row, stale version):
// the SQL has no RETURNING row then, and the repository maps that to 0
// instead of throwing.
public sealed partial class InMemoryMonzeState
{
    // welcome_delivery (clan_id, user_id) -> claimed_at.
    private readonly Dictionary<(long ClanId, long UserId), DateTimeOffset> _welcomeDeliveries = new();

    public Task<long> SetWelcomeAsync(
        long clanId,
        long actorUserId,
        bool enabled,
        string? text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!IsAdminLocked(clanId, actorUserId))
            {
                return Task.FromResult(0L);
            }

            if (!_settings.TryGetValue(clanId, out var row))
            {
                _settings.Add(clanId, new SettingsRow { WelcomeEnabled = enabled, WelcomeText = text });
                return Task.FromResult(1L);
            }

            row.WelcomeEnabled = enabled;
            row.WelcomeText = text ?? row.WelcomeText;
            row.Version++;
            return Task.FromResult(row.Version);
        }
    }

    public Task<long> SetWelcomeMessageAsync(
        long clanId,
        long actorUserId,
        string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!IsAdminLocked(clanId, actorUserId))
            {
                return Task.FromResult(0L);
            }

            if (!_settings.TryGetValue(clanId, out var row))
            {
                _settings.Add(clanId, new SettingsRow { WelcomeText = text });
                return Task.FromResult(1L);
            }

            row.WelcomeText = text;
            row.Version++;
            return Task.FromResult(row.Version);
        }
    }

    public Task<long> RemoveWelcomeMessageAsync(
        long clanId,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // UPDATE only: no settings row means 0 even for an admin. The
            // version moves even when the text was already NULL.
            if (!IsAdminLocked(clanId, actorUserId) || !_settings.TryGetValue(clanId, out var row))
            {
                return Task.FromResult(0L);
            }

            row.WelcomeText = null;
            row.Version++;
            return Task.FromResult(row.Version);
        }
    }

    public Task<long> SetWelcomeConfigurationAsync(
        long clanId,
        long actorUserId,
        bool enabled,
        string? text,
        WelcomeEmbedSettings embed,
        CancellationToken cancellationToken,
        long? expectedVersion = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var embedJson = JsonSerializer.Serialize(embed);
        lock (_gate)
        {
            _settings.TryGetValue(clanId, out var row);

            // COALESCE((SELECT version ...), 0) = @expected: a missing row
            // matches only expectedVersion 0.
            if (expectedVersion is { } expected && (row?.Version ?? 0) != expected)
            {
                return Task.FromResult(0L);
            }

            if (!IsAdminLocked(clanId, actorUserId))
            {
                return Task.FromResult(0L);
            }

            if (row is null)
            {
                _settings.Add(clanId, new SettingsRow
                {
                    WelcomeEnabled = enabled,
                    WelcomeText = text,
                    WelcomeEmbedJson = embedJson
                });
                return Task.FromResult(1L);
            }

            row.WelcomeEnabled = enabled;
            row.WelcomeText = text ?? row.WelcomeText;
            row.WelcomeEmbedJson = embedJson;
            row.Version++;
            return Task.FromResult(row.Version);
        }
    }

    public Task<long> RemoveWelcomeEmbedAsync(
        long clanId,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!IsAdminLocked(clanId, actorUserId) || !_settings.TryGetValue(clanId, out var row))
            {
                return Task.FromResult(0L);
            }

            row.WelcomeEmbedJson = null;
            row.Version++;
            return Task.FromResult(row.Version);
        }
    }

    public Task<WelcomeSettings?> GetWelcomeAsync(long clanId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // No activity or authorization filter: any clan_settings row.
            if (!_settings.TryGetValue(clanId, out var row))
            {
                return Task.FromResult<WelcomeSettings?>(null);
            }

            var embed = row.WelcomeEmbedJson is null
                ? null
                : JsonSerializer.Deserialize<WelcomeEmbedSettings>(row.WelcomeEmbedJson);
            return Task.FromResult<WelcomeSettings?>(
                new WelcomeSettings(row.WelcomeEnabled, row.WelcomeText, row.Version, embed));
        }
    }

    public Task<bool> TryClaimWelcomeAsync(long clanId, long userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // welcome_delivery has no clan_registry reference or actor check.
            return Task.FromResult(_welcomeDeliveries.TryAdd((clanId, userId), NowLocked()));
        }
    }

    public Task ReleaseWelcomeClaimAsync(long clanId, long userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _welcomeDeliveries.Remove((clanId, userId));
        }

        return Task.CompletedTask;
    }
}
