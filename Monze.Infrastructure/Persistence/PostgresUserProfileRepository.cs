using Monze.Application;
using Npgsql;
using NpgsqlTypes;

namespace Monze.Infrastructure.Persistence;

public sealed class PostgresUserProfileRepository : IUserProfileRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresUserProfileRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task UpsertAsync(
        long clanId,
        long userId,
        string? clanNick,
        string? displayName,
        string? username,
        string? avatarUrl,
        CancellationToken cancellationToken)
    {
        if (clanId <= 0 || userId <= 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO clan_user_profile(
                clan_id, user_id, clan_nick, display_name, username, avatar_url, version, updated_at)
            VALUES (@clan, @user, @clan_nick, @display_name, @username, @avatar, 1, now())
            ON CONFLICT (clan_id, user_id) DO UPDATE
            SET clan_nick = COALESCE(NULLIF(EXCLUDED.clan_nick, ''), clan_user_profile.clan_nick),
                display_name = COALESCE(NULLIF(EXCLUDED.display_name, ''), clan_user_profile.display_name),
                username = COALESCE(NULLIF(EXCLUDED.username, ''), clan_user_profile.username),
                avatar_url = NULLIF(EXCLUDED.avatar_url, ''),
                version = clan_user_profile.version + 1,
                updated_at = now()
            WHERE clan_user_profile.clan_nick IS DISTINCT FROM COALESCE(NULLIF(EXCLUDED.clan_nick, ''), clan_user_profile.clan_nick)
               OR clan_user_profile.display_name IS DISTINCT FROM COALESCE(NULLIF(EXCLUDED.display_name, ''), clan_user_profile.display_name)
               OR clan_user_profile.username IS DISTINCT FROM COALESCE(NULLIF(EXCLUDED.username, ''), clan_user_profile.username)
               OR clan_user_profile.avatar_url IS DISTINCT FROM NULLIF(EXCLUDED.avatar_url, '');
            """,
            connection);
        AddParameters(command, clanId, userId, clanNick, displayName, username, avatarUrl);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<UserProfileSnapshot?> GetByIdAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        if (clanId <= 0 || userId <= 0)
        {
            return null;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT clan_id, user_id, clan_nick, display_name, username, avatar_url, updated_at
            FROM clan_user_profile
            WHERE clan_id = @clan AND user_id = @user;
            """,
            connection);
        command.Parameters.AddWithValue("clan", NpgsqlDbType.Bigint, clanId);
        command.Parameters.AddWithValue("user", NpgsqlDbType.Bigint, userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadSnapshot(reader)
            : null;
    }

    private static UserProfileSnapshot ReadSnapshot(NpgsqlDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            ReadString(reader, 2),
            ReadString(reader, 3),
            ReadString(reader, 4),
            ReadString(reader, 5),
            reader.GetFieldValue<DateTimeOffset>(6));

    private static string? ReadString(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddParameters(
        NpgsqlCommand command,
        long clanId,
        long userId,
        string? clanNick,
        string? displayName,
        string? username,
        string? avatarUrl)
    {
        command.Parameters.AddWithValue("clan", NpgsqlDbType.Bigint, clanId);
        command.Parameters.AddWithValue("user", NpgsqlDbType.Bigint, userId);
        command.Parameters.AddWithValue("clan_nick", NpgsqlDbType.Text, clanNick?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("display_name", NpgsqlDbType.Text, displayName?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("username", NpgsqlDbType.Text, username?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("avatar", NpgsqlDbType.Text, avatarUrl?.Trim() ?? string.Empty);
    }
}
