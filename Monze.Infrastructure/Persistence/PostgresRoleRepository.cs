using Monze.Application;
using Monze.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;

public sealed class PostgresRoleRepository : IRoleRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresRoleRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<bool> IsRoleAutomationEnabledAsync(long clanId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT role_enabled FROM clan_settings WHERE clan_id = @clan;", connection);
        command.Parameters.AddWithValue("clan", clanId);
        return (bool?)await command.ExecuteScalarAsync(cancellationToken) ?? true;
    }

    public async Task<bool> SetRoleAutomationEnabledAsync(
        long clanId,
        long actorUserId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO clan_settings(clan_id, role_enabled)
            SELECT @clan, @enabled
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
            SET role_enabled = EXCLUDED.role_enabled,
                version = clan_settings.version + 1
            RETURNING clan_id;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("actor", actorUserId);
        command.Parameters.AddWithValue("enabled", enabled);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<IReadOnlyList<AutoRoleRule>> ListEnabledRoleRulesAsync(
        long clanId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT r.clan_id, r.role_id, r.rule_kind, r.condition_value, r.version, r.updated_at
            FROM role_rule r
            JOIN clan_registry c ON c.clan_id = r.clan_id
            JOIN clan_settings s ON s.clan_id = r.clan_id
            WHERE r.clan_id = @clan
              AND s.role_enabled
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
            SELECT r.clan_id, r.role_id, r.rule_kind, r.condition_value, r.version, r.updated_at
            FROM role_rule r
            JOIN clan_registry c ON c.clan_id = r.clan_id
            JOIN clan_settings s ON s.clan_id = r.clan_id
            WHERE r.enabled
              AND s.role_enabled
              AND c.inactive_reason IS NULL
            ORDER BY r.clan_id, r.role_id, r.rule_kind;
            """, connection);
        return await ReadRoleRulesAsync(command, cancellationToken);
    }

    public async Task<bool> SetRoleRuleAsync(
        long clanId,
        long actorUserId,
        long roleId,
        RoleRuleKind kind,
        string? conditionValue,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO role_rule(clan_id, role_id, rule_kind, enabled, condition_value)
            SELECT @clan, @role, @kind, TRUE, @condition
            WHERE EXISTS (
              SELECT 1
              FROM clan_registry c
              LEFT JOIN clan_admin a
                ON a.clan_id = c.clan_id AND a.user_id = @actor
              WHERE c.clan_id = @clan
                AND c.inactive_reason IS NULL
                AND (c.owner_id = @actor OR a.user_id IS NOT NULL)
            )
            ON CONFLICT (clan_id, role_id, rule_kind)
            DO UPDATE SET enabled = EXCLUDED.enabled,
                          condition_value = EXCLUDED.condition_value,
                          version = role_rule.version + 1,
                          updated_at = now()
            RETURNING clan_id;
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("actor", actorUserId);
        command.Parameters.AddWithValue("role", roleId);
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text)
        {
            Value = RoleRules.ToStorageName(kind)
        });
        command.Parameters.Add(new NpgsqlParameter("condition", NpgsqlDbType.Text)
        {
            Value = (object?)conditionValue ?? DBNull.Value
        });
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<bool> RemoveRoleRuleAsync(
        long clanId,
        long actorUserId,
        long roleId,
        RoleRuleKind kind,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM role_rule
            WHERE clan_id = @clan
              AND role_id = @role
              AND rule_kind = @kind
              AND EXISTS (
                SELECT 1
                FROM clan_registry c
                LEFT JOIN clan_admin a
                  ON a.clan_id = c.clan_id AND a.user_id = @actor
                WHERE c.clan_id = @clan
                  AND c.inactive_reason IS NULL
                  AND (c.owner_id = @actor OR a.user_id IS NOT NULL)
              );
            """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("actor", actorUserId);
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
                reader.GetInt64(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return rules;
    }

    private static bool TryParseStorageName(string value, out RoleRuleKind kind)
        => RoleRules.TryParseKind(value, out kind);

}
