using Monze.Simulator;
using Monze.Testing.Postgres;
using Npgsql;

namespace Monze.Tests.E2E.Harness;

/// <summary>
/// The platform the area scenarios start from. Clan A has an owner, an admin
/// (a delegate when <see cref="SeedDelegateAsync"/> runs), two members with
/// different tenure, a "general" and a "lobby" (welcome) text channel, one
/// voice room and two roles. Clan B belongs to an outsider who is not in
/// clan A. Clan C exists but does not have the bot yet. One direct-message
/// channel connects the member and the bot. The bot token is the secret
/// canary. Ids are 19-digit snowflake-sized values.
/// </summary>
internal static class AreaWorld
{
    public const long BotId = 1_840_000_000_000_009_101L;
    public const long OwnerId = 1_840_000_000_000_000_201L;
    public const long AdminId = 1_840_000_000_000_000_202L;
    public const long MemberId = 1_840_000_000_000_000_203L;
    public const long Member2Id = 1_840_000_000_000_000_204L;
    public const long OutsiderId = 1_840_000_000_000_000_205L;
    public const long JoinerId = 1_840_000_000_000_000_206L;
    public const long OtherBotId = 1_840_000_000_000_000_207L;
    public const long LateOwnerId = 1_840_000_000_000_000_208L;
    public const long ClanId = 1_840_000_000_000_005_101L;
    public const long OtherClanId = 1_840_000_000_000_005_102L;
    public const long LateClanId = 1_840_000_000_000_005_103L;
    public const long GeneralId = 1_840_000_000_000_006_101L;
    public const long LobbyId = 1_840_000_000_000_006_102L;
    public const long VoiceId = 1_840_000_000_000_006_103L;
    public const long OtherGeneralId = 1_840_000_000_000_006_201L;
    public const long LateGeneralId = 1_840_000_000_000_006_401L;
    public const long DirectId = 1_840_000_000_000_006_301L;
    public const long DeveloperRoleId = 1_840_000_000_000_007_101L;
    public const long VeteranRoleId = 1_840_000_000_000_007_102L;
    public const string VoiceLabel = "Daily Standup";
    public const string MemberNick = "member-nick";
    public const string MemberAvatar = "https://cdn.example.test/avatars/member.png";

    public static SimWorld Create()
    {
        var now = DateTimeOffset.UtcNow;
        var world = new SimWorld()
            .WithBot(BotId, "monze-area", E2ECanaries.MezonToken, "Monze")
            .User(OwnerId, "owner", "Clan Owner")
            .User(AdminId, "admin", "Clan Admin")
            .User(MemberId, "member", "Member One", avatar: MemberAvatar)
            .User(Member2Id, "member2", "Member Two")
            .User(OutsiderId, "outsider", "Out Sider", avatar: "https://cdn.example.test/avatars/outsider.png")
            .User(JoinerId, "joiner", "New Joiner")
            .User(OtherBotId, "helper-bot", "Helper Bot", isBot: true)
            .User(LateOwnerId, "late-owner", "Late Owner");
        world.Clan(ClanId, "Area Clan", OwnerId)
            .TextChannel(GeneralId, "general")
            .TextChannel(LobbyId, "lobby")
            .VoiceChannel(VoiceId, VoiceLabel)
            .Role(DeveloperRoleId, "Developer")
            .Role(VeteranRoleId, "Veteran")
            .WelcomeChannel(LobbyId)
            .Member(AdminId, clanNick: "admin-nick", joinedAt: now.AddDays(-40), VeteranRoleId)
            .Member(MemberId, clanNick: MemberNick, joinedAt: now.AddDays(-40))
            .Member(Member2Id, joinedAt: now.AddDays(-1));
        world.Clan(OtherClanId, "Other Clan", OutsiderId)
            .TextChannel(OtherGeneralId, "general");
        world.Clan(LateClanId, "Late Clan", LateOwnerId)
            .TextChannel(LateGeneralId, "general")
            .WithoutBot();
        world.DirectChannel(DirectId, MemberId);
        return world;
    }

    /// <summary>Registers clan A with its owner and makes the admin a delegate before Monze starts.</summary>
    public static async Task SeedDelegateAsync(CampaignDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO clan_registry(clan_id, owner_id) VALUES (@clan, @owner);
            INSERT INTO clan_settings(clan_id) VALUES (@clan);
            INSERT INTO clan_admin(clan_id, user_id) VALUES (@clan, @admin);
            """, connection);
        command.Parameters.AddWithValue("clan", ClanId);
        command.Parameters.AddWithValue("owner", OwnerId);
        command.Parameters.AddWithValue("admin", AdminId);
        await command.ExecuteNonQueryAsync();
    }
}
