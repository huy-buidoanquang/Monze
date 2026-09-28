using Monze.Application;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;
public sealed class PostgresAiUsageRepository : IAiUsageRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAiUsageRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<(bool Allowed, int Used)> ConsumeAiAsync(long clanId, long userId, int tokens, int dailyCap, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var write = new NpgsqlCommand("""
            INSERT INTO ai_usage(clan_id, user_id, usage_day, tokens)
            SELECT @clan, @user, CURRENT_DATE, @requested
            WHERE @requested <= @cap
            ON CONFLICT (clan_id, user_id, usage_day) DO UPDATE
            SET tokens = ai_usage.tokens + EXCLUDED.tokens
            WHERE ai_usage.tokens + EXCLUDED.tokens <= @cap
            RETURNING tokens;
            """, connection);
        write.Parameters.AddWithValue("clan", clanId);
        write.Parameters.AddWithValue("user", userId);
        write.Parameters.AddWithValue("requested", Math.Max(1, tokens));
        write.Parameters.AddWithValue("cap", dailyCap);
        var value = await write.ExecuteScalarAsync(cancellationToken);
        return value is null ? (false, 0) : (true, (int)value);
    }

}

