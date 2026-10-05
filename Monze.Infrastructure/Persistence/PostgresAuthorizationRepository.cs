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
        long actorUserId,
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
                WHERE clan_id = @clan
                  AND owner_id = @actor
                  AND inactive_reason IS NULL
              )
              ON CONFLICT (clan_id, user_id) DO NOTHING;
              """
            : """
              DELETE FROM clan_admin
              WHERE clan_id = @clan
                AND user_id = @user
                AND EXISTS (
                  SELECT 1 FROM clan_registry
                  WHERE clan_id = @clan
                    AND owner_id = @actor
                    AND inactive_reason IS NULL
                );
              """, connection);
        command.Parameters.AddWithValue("clan", clanId);
        command.Parameters.AddWithValue("actor", actorUserId);
        command.Parameters.AddWithValue("user", userId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

}
