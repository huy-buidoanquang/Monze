using Monze.Application;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;
public sealed partial class PostgresCommunityRepository : ICommunityRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCommunityRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task AddFaqAsync(long clanId, string question, string answer, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO knowledge_entry(clan_id, question, answer, normalized)
            VALUES (@clan, @q, @a, @norm);
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("q", question);
        command.Parameters.AddWithValue("a", answer);
        command.Parameters.AddWithValue("norm", question.ToLowerInvariant());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> FindFaqAsync(long clanId, string query, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT answer FROM knowledge_entry
            WHERE clan_id = @clan
              AND to_tsvector('simple', normalized) @@ plainto_tsquery('simple', @q)
            ORDER BY id DESC LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("q", query);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<(bool Applied, long Balance)> AddPointsAsync(long clanId, long userId, long delta, string sourceType, string sourceId, int dailyCap, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO activity_balance(clan_id, user_id, points, awarded_today, award_day)
            VALUES (@clan, @user, 0, 0, CURRENT_DATE)
            ON CONFLICT (clan_id, user_id) DO NOTHING;
            """, connection, tx))
        {
            seed.Parameters.AddWithValue("clan", clanId);
            seed.Parameters.AddWithValue("user", userId);
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var current = new NpgsqlCommand("""
            SELECT points, awarded_today, award_day FROM activity_balance
            WHERE clan_id = @clan AND user_id = @user FOR UPDATE;
            """, connection, tx);
        current.Parameters.AddWithValue("clan", clanId);
        current.Parameters.AddWithValue("user", userId);
        long balance = 0;
        var awarded = 0;
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        await using (var reader = await current.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                balance = reader.GetInt64(0);
                awarded = reader.GetInt32(1);
                day = DateOnly.FromDateTime(reader.GetDateTime(2));
            }
        }

        if (day != DateOnly.FromDateTime(DateTime.UtcNow))
        {
            awarded = 0;
        }

        if (!PointsLedger.TryApply(balance, delta, awarded, dailyCap, false, out _))
        {
            await tx.CommitAsync(cancellationToken);
            return (false, balance);
        }

        await using var insert = new NpgsqlCommand("""
            INSERT INTO activity_ledger(clan_id, user_id, source_type, source_id, delta)
            VALUES (@clan, @user, @type, @source, @delta)
            ON CONFLICT (clan_id, user_id, source_type, source_id) DO NOTHING
            RETURNING id;
            """, connection, tx);
        insert.Parameters.AddWithValue("clan", clanId);
        insert.Parameters.AddWithValue("user", userId);
        insert.Parameters.AddWithValue("type", sourceType);
        insert.Parameters.AddWithValue("source", sourceId);
        insert.Parameters.AddWithValue("delta", delta);
        var inserted = await insert.ExecuteScalarAsync(cancellationToken);
        if (inserted is null)
        {
            await tx.CommitAsync(cancellationToken);
            return (false, balance);
        }

        await using var update = new NpgsqlCommand("""
            UPDATE activity_balance
            SET points = points + @delta,
                awarded_today = @awarded,
                award_day = CURRENT_DATE
            WHERE clan_id = @clan AND user_id = @user
            RETURNING points;
            """, connection, tx);
        update.Parameters.AddWithValue("clan", clanId);
        update.Parameters.AddWithValue("user", userId);
        update.Parameters.AddWithValue("delta", delta);
        update.Parameters.AddWithValue("awarded", awarded + (delta > 0 ? 1 : 0));
        var next = (long)(await update.ExecuteScalarAsync(cancellationToken) ?? balance);
        await tx.CommitAsync(cancellationToken);
        return (true, next);
    }

    public async Task<IReadOnlyList<long>> UsersWithMinimumPointsAsync(
        long clanId,
        long minimum,
        CancellationToken cancellationToken)
    {
        var users = new List<long>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT user_id
            FROM activity_balance
            WHERE clan_id = @clan AND points >= @minimum
            ORDER BY user_id;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("minimum", minimum);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(reader.GetInt64(0));
        }

        return users;
    }

    public async Task<IReadOnlyList<(long UserId, long Points)>> LeaderboardAsync(long clanId, int limit, CancellationToken cancellationToken)
    {
        var rows = new List<(long, long)>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT user_id, points FROM activity_balance WHERE clan_id = @clan ORDER BY points DESC, user_id ASC LIMIT @limit;", connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        return rows;
    }

    public async Task<int?> SpinAsync(long clanId, long userId, IReadOnlyList<int> weights, int roll, CancellationToken cancellationToken)
    {
        var index = Wheel.PickIndex(weights, roll);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var claim = new NpgsqlCommand("""
            INSERT INTO wheel_cooldown(clan_id, user_id, next_allowed_at)
            VALUES (@clan, @user, now() + interval '1 minute')
            ON CONFLICT (clan_id, user_id)
            DO UPDATE SET next_allowed_at = EXCLUDED.next_allowed_at
            WHERE wheel_cooldown.next_allowed_at <= now()
            RETURNING 1;
            """, connection, transaction))
        {
            claim.Parameters.AddWithValue("clan", clanId);
            claim.Parameters.AddWithValue("user", userId);
            if (await claim.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await using (var attempt = new NpgsqlCommand("""
            INSERT INTO game_attempt(clan_id, user_id, prize_index)
            VALUES (@clan, @user, @index);
            """, connection, transaction))
        {
            attempt.Parameters.AddWithValue("clan", clanId);
            attempt.Parameters.AddWithValue("user", userId);
            attempt.Parameters.AddWithValue("index", index);
            await attempt.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return index;
    }

    public async Task<string?> NextTopicAsync(long clanId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long topicId;
        string topicText;
        await using (var select = new NpgsqlCommand("""
            SELECT id, text
            FROM topic_prompt
            WHERE clan_id = @clan
            ORDER BY last_used_at NULLS FIRST, id
            LIMIT 1
            FOR UPDATE SKIP LOCKED;
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("clan", clanId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            topicId = reader.GetInt64(0);
            topicText = reader.GetString(1);
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE topic_prompt SET last_used_at = now() WHERE id = @id;",
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("id", topicId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return topicText;
    }

    public async Task<bool> AddTopicAsync(long clanId, string text, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO topic_prompt(clan_id, text)
            VALUES (@clan, @text)
            ON CONFLICT (clan_id, text) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("text", text);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> RemoveTopicAsync(long clanId, string text, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM topic_prompt
            WHERE clan_id = @clan AND text = @text;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("text", text);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

}

