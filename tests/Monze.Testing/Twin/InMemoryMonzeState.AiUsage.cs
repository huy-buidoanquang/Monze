namespace Monze.Testing.Twin;

// Mirrors Monze.Infrastructure/Persistence/PostgresAiUsageRepository.cs.
public sealed partial class InMemoryMonzeState
{
    // ai_usage (clan_id, user_id, usage_day) -> tokens.
    private readonly Dictionary<(long ClanId, long UserId, DateOnly Day), int> _aiUsage = new();

    public Task<(bool Allowed, int Used)> ConsumeAiAsync(
        long clanId,
        long userId,
        int tokens,
        int dailyCap,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = Math.Max(1, tokens);
        lock (_gate)
        {
            // A refused request reports (false, 0), not the tokens used so far.
            if (requested > dailyCap)
            {
                return Task.FromResult((false, 0));
            }

            var key = (clanId, userId, CurrentDateLocked());
            if (!_aiUsage.TryGetValue(key, out var used))
            {
                _aiUsage.Add(key, requested);
                return Task.FromResult((true, requested));
            }

            // int + int overflows with "integer out of range" in PostgreSQL.
            var total = checked(used + requested);
            if (total > dailyCap)
            {
                return Task.FromResult((false, 0));
            }

            _aiUsage[key] = total;
            return Task.FromResult((true, total));
        }
    }

    public Task RefundAiAsync(long clanId, long userId, int tokens, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (clanId, userId, CurrentDateLocked());
            if (_aiUsage.TryGetValue(key, out var used))
            {
                _aiUsage[key] = Math.Max(0, used - Math.Max(1, tokens));
            }
        }

        return Task.CompletedTask;
    }

    // CURRENT_DATE is the date in the PostgreSQL session TimeZone, which
    // Monze never sets (server default applies). The twin takes that zone
    // from TimeProvider.LocalTimeZone (UTC for ManualTimeProvider).
    private DateOnly CurrentDateLocked()
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).DateTime);
}
