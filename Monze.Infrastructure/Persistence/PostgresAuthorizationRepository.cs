using Monze.Application;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace Monze.Infrastructure.Persistence;
public sealed class PostgresAuthorizationRepository : IAuthorizationRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAuthorizationRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<bool> IsOwnerAsync(long clanId, long userId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1
              FROM clan_registry
              WHERE clan_id = @clan
                AND owner_id = @user
                AND inactive_reason IS NULL
            );
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("user", userId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<bool> IsAdminAsync(long clanId, long userId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1
              FROM clan_registry c
              LEFT JOIN clan_admin a
                ON a.clan_id = c.clan_id AND a.user_id = @user
              WHERE c.clan_id = @clan
                AND c.inactive_reason IS NULL
                AND (c.owner_id = @user OR a.user_id IS NOT NULL)
            );
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("user", userId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<bool> SetDelegateAsync(
        long clanId,
        long userId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(enabled
            ? """
              INSERT INTO clan_admin(clan_id, user_id)
              SELECT @clan, @user
              WHERE EXISTS (
                SELECT 1 FROM clan_registry
                WHERE clan_id = @clan AND inactive_reason IS NULL
              )
              ON CONFLICT (clan_id, user_id) DO NOTHING;
              """
            : """
              DELETE FROM clan_admin
              WHERE clan_id = @clan AND user_id = @user;
              """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("user", userId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<long> SetWelcomeAsync(long clanId, bool enabled, string? text, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO clan_settings(clan_id, welcome_enabled, welcome_text)
            VALUES (@clan, @enabled, @text)
            ON CONFLICT (clan_id) DO UPDATE
            SET welcome_enabled = EXCLUDED.welcome_enabled,
                welcome_text = COALESCE(EXCLUDED.welcome_text, clan_settings.welcome_text),
                version = clan_settings.version + 1
            RETURNING version;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text)
        {
            Value = (object?)text ?? DBNull.Value
        });
        var version = await command.ExecuteScalarAsync(cancellationToken);
        return version is long value ? value : 0;
    }

    public async Task<long> SetWelcomeEmbedAsync(
        long clanId,
        WelcomeEmbedSettings embed,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO clan_settings(clan_id, welcome_embed)
            VALUES (@clan, @embed)
            ON CONFLICT (clan_id) DO UPDATE
            SET welcome_embed = EXCLUDED.welcome_embed,
                version = clan_settings.version + 1
            RETURNING version;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.Add(new NpgsqlParameter("embed", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(embed)
        });
        var version = await command.ExecuteScalarAsync(cancellationToken);
        return version is long value ? value : 0;
    }

    public async Task<long> SetWelcomeConfigurationAsync(
        long clanId,
        bool enabled,
        string? text,
        WelcomeEmbedSettings embed,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO clan_settings(clan_id, welcome_enabled, welcome_text, welcome_embed)
            VALUES (@clan, @enabled, @text, @embed)
            ON CONFLICT (clan_id) DO UPDATE
            SET welcome_enabled = EXCLUDED.welcome_enabled,
                welcome_text = COALESCE(EXCLUDED.welcome_text, clan_settings.welcome_text),
                welcome_embed = EXCLUDED.welcome_embed,
                version = clan_settings.version + 1
            RETURNING version;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("enabled", enabled);
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

    public async Task<bool> IsRoleSelfAssignableAsync(
        long clanId,
        long roleId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT enabled
            FROM role_rule
            WHERE clan_id = @clan AND role_id = @role AND rule_kind = 'self_select';
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("role", roleId);
        return (bool?)await command.ExecuteScalarAsync(cancellationToken) ?? false;
    }

    public async Task SetRoleSelfAssignableAsync(
        long clanId,
        long roleId,
        bool enabled,
        CancellationToken cancellationToken)
        => await SetRoleRuleAsync(
            clanId,
            roleId,
            RoleRuleKind.SelfSelect,
            null,
            cancellationToken,
            enabled);

    public async Task<IReadOnlyList<AutoRoleRule>> ListEnabledRoleRulesAsync(
        long clanId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT r.clan_id, r.role_id, r.rule_kind, r.condition_value, r.version
            FROM role_rule r
            JOIN clan_registry c ON c.clan_id = r.clan_id
            WHERE r.clan_id = @clan
              AND r.enabled
              AND c.inactive_reason IS NULL;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        return await ReadRoleRulesAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<AutoRoleRule>> ListEnabledRoleRulesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT r.clan_id, r.role_id, r.rule_kind, r.condition_value, r.version
            FROM role_rule r
            JOIN clan_registry c ON c.clan_id = r.clan_id
            WHERE r.enabled
              AND c.inactive_reason IS NULL
            ORDER BY r.clan_id, r.role_id, r.rule_kind;
            """, connection);
        return await ReadRoleRulesAsync(command, cancellationToken);
    }

    public async Task SetRoleRuleAsync(
        long clanId,
        long roleId,
        RoleRuleKind kind,
        string? conditionValue,
        CancellationToken cancellationToken)
        => await SetRoleRuleAsync(clanId, roleId, kind, conditionValue, cancellationToken, true);

    private async Task SetRoleRuleAsync(
        long clanId,
        long roleId,
        RoleRuleKind kind,
        string? conditionValue,
        CancellationToken cancellationToken,
        bool enabled)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO role_rule(clan_id, role_id, rule_kind, enabled, condition_value)
            VALUES (@clan, @role, @kind, @enabled, @condition)
            ON CONFLICT (clan_id, role_id, rule_kind)
            DO UPDATE SET enabled = EXCLUDED.enabled,
                          condition_value = EXCLUDED.condition_value,
                          version = role_rule.version + 1,
                          updated_at = now();
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("role", roleId);
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text)
        {
            Value = RoleRules.ToStorageName(kind)
        });
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.Add(new NpgsqlParameter("condition", NpgsqlDbType.Text)
        {
            Value = (object?)conditionValue ?? DBNull.Value
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RemoveRoleRuleAsync(
        long clanId,
        long roleId,
        RoleRuleKind kind,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM role_rule
            WHERE clan_id = @clan
              AND role_id = @role
              AND rule_kind = @kind;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("role", roleId);
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text)
        {
            Value = RoleRules.ToStorageName(kind)
        });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task RecordRoleGrantAsync(
        long clanId,
        long roleId,
        long userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO role_grant(clan_id, role_id, user_id)
            VALUES (@clan, @role, @user)
            ON CONFLICT (clan_id, role_id, user_id) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("role", roleId);
        command.Parameters.AddWithValue("user", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<AutoRoleRule>> ReadRoleRulesAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var rules = new List<AutoRoleRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!TryParseStorageName(reader.GetString(2), out var kind))
            {
                continue;
            }

            rules.Add(new AutoRoleRule(
                reader.GetInt64(0),
                reader.GetInt64(1),
                kind,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4)));
        }

        return rules;
    }

    private static bool TryParseStorageName(string value, out RoleRuleKind kind)
        => RoleRules.TryParseKind(value, out kind);

}

