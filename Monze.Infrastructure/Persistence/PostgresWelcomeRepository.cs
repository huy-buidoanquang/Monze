using Monze.Application;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace Monze.Infrastructure.Persistence;

public sealed class PostgresWelcomeRepository : IWelcomeRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresWelcomeRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<long> SetWelcomeAsync(long clanId, long actorUserId, bool enabled, string? text, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO clan_settings(clan_id, welcome_enabled, welcome_text)
            SELECT @clan, @enabled, @text
            WHERE EXISTS (
              SELECT 1
              FROM clan_registry c
              LEFT JOIN clan_admin a
                ON a.clan_id = c.clan_id AND a.user_id = @actor
              WHERE c.clan_id = @clan
                AND c.inactive_reason IS NULL
                AND (c.owner_id = @actor OR a.user_id IS NOT NULL)
            )
            ON CONFLICT (clan_id) DO UPDATE
            SET welcome_enabled = EXCLUDED.welcome_enabled,
                welcome_text = COALESCE(EXCLUDED.welcome_text, clan_settings.welcome_text),
                version = clan_settings.version + 1
            RETURNING version;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("actor", actorUserId);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text)
        {
            Value = (object?)text ?? DBNull.Value
        });
        var version = await command.ExecuteScalarAsync(cancellationToken);
        return version is long value ? value : 0;
    }

    public async Task<long> SetWelcomeMessageAsync(long clanId, long actorUserId, string text, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO clan_settings(clan_id, welcome_text)
            SELECT @clan, @text
            WHERE EXISTS (
              SELECT 1
              FROM clan_registry c
              LEFT JOIN clan_admin a
                ON a.clan_id = c.clan_id AND a.user_id = @actor
              WHERE c.clan_id = @clan
                AND c.inactive_reason IS NULL
                AND (c.owner_id = @actor OR a.user_id IS NOT NULL)
            )
            ON CONFLICT (clan_id) DO UPDATE
            SET welcome_text = EXCLUDED.welcome_text,
                version = clan_settings.version + 1
            RETURNING version;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("actor", actorUserId);
        command.Parameters.AddWithValue("text", text);
        var version = await command.ExecuteScalarAsync(cancellationToken);
        return version is long value ? value : 0;
    }

    public async Task<long> RemoveWelcomeMessageAsync(long clanId, long actorUserId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE clan_settings
            SET welcome_text = NULL,
                version = version + 1
            WHERE clan_id = @clan
              AND EXISTS (
                SELECT 1
                FROM clan_registry c
                LEFT JOIN clan_admin a
                  ON a.clan_id = c.clan_id AND a.user_id = @actor
                WHERE c.clan_id = @clan
                  AND c.inactive_reason IS NULL
                  AND (c.owner_id = @actor OR a.user_id IS NOT NULL)
              )
            RETURNING version;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("actor", actorUserId);
        var version = await command.ExecuteScalarAsync(cancellationToken);
        return version is long value ? value : 0;
    }

    public async Task<long> SetWelcomeConfigurationAsync(
        long clanId,
        long actorUserId,
        bool enabled,
        string? text,
        WelcomeEmbedSettings embed,
        CancellationToken cancellationToken,
        long? expectedVersion = null)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO clan_settings(clan_id, welcome_enabled, welcome_text, welcome_embed)
            SELECT @clan, @enabled, @text, @embed
            WHERE (@expected IS NULL OR COALESCE(
                (SELECT version FROM clan_settings WHERE clan_id = @clan), 0) = @expected)
            AND EXISTS (
              SELECT 1
              FROM clan_registry c
              LEFT JOIN clan_admin a
                ON a.clan_id = c.clan_id AND a.user_id = @actor
              WHERE c.clan_id = @clan
                AND c.inactive_reason IS NULL
                AND (c.owner_id = @actor OR a.user_id IS NOT NULL)
            )
            ON CONFLICT (clan_id) DO UPDATE
            SET welcome_enabled = EXCLUDED.welcome_enabled,
                welcome_text = COALESCE(EXCLUDED.welcome_text, clan_settings.welcome_text),
                welcome_embed = EXCLUDED.welcome_embed,
                version = clan_settings.version + 1
            WHERE @expected IS NULL OR clan_settings.version = @expected
            RETURNING version;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("actor", actorUserId);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.Add(new NpgsqlParameter("expected", NpgsqlDbType.Bigint)
        {
            Value = (object?)expectedVersion ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text)
        {
            Value = (object?)text ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("embed", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(embed)
        });
        var version = await command.ExecuteScalarAsync(cancellationToken);
        return version is long value ? value : 0;
    }

    public async Task<long> RemoveWelcomeEmbedAsync(long clanId, long actorUserId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE clan_settings
            SET welcome_embed = NULL,
                version = version + 1
            WHERE clan_id = @clan
              AND EXISTS (
                SELECT 1
                FROM clan_registry c
                LEFT JOIN clan_admin a
                  ON a.clan_id = c.clan_id AND a.user_id = @actor
                WHERE c.clan_id = @clan
                  AND c.inactive_reason IS NULL
                  AND (c.owner_id = @actor OR a.user_id IS NOT NULL)
              )
            RETURNING version;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("actor", actorUserId);
        var version = await command.ExecuteScalarAsync(cancellationToken);
        return version is long value ? value : 0;
    }

    public async Task<WelcomeSettings?> GetWelcomeAsync(
        long clanId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT welcome_enabled, welcome_text, version, welcome_embed::text
            FROM clan_settings
            WHERE clan_id = @clan;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        WelcomeEmbedSettings? embed = null;
        if (!reader.IsDBNull(3))
        {
            embed = JsonSerializer.Deserialize<WelcomeEmbedSettings>(reader.GetString(3));
        }

        return new WelcomeSettings(
            reader.GetBoolean(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2),
            embed);
    }

    public async Task<bool> TryClaimWelcomeAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO welcome_delivery(clan_id, user_id)
            VALUES (@clan, @user)
            ON CONFLICT (clan_id, user_id) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("user", userId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task ReleaseWelcomeClaimAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM welcome_delivery
            WHERE clan_id = @clan AND user_id = @user;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("user", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
