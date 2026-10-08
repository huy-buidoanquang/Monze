using Monze.Simulator;

namespace Monze.Tests.E2E.Harness;

/// <summary>
/// The platform state the smoke tests start from: one clan owned by a human
/// owner, a member, the bot, a "general" text channel and one empty voice
/// room. Ids are 19-digit snowflake-sized values like real Mezon ids.
/// </summary>
internal static class SmokeWorld
{
    public const long BotId = 1_840_000_000_000_009_001L;
    public const string BotToken = "simulated-bot-token-not-a-secret";
    public const long OwnerId = 1_840_000_000_000_000_101L;
    public const long MemberId = 1_840_000_000_000_000_102L;
    public const long ClanId = 1_840_000_000_000_005_001L;
    public const long GeneralId = 1_840_000_000_000_006_001L;
    public const long VoiceId = 1_840_000_000_000_006_002L;
    public const long RoleId = 1_840_000_000_000_007_001L;
    public const string VoiceLabel = "Daily Standup";

    public static SimWorld Create()
    {
        var world = new SimWorld()
            .WithBot(BotId, "monze-sim", BotToken, "Monze")
            .User(OwnerId, "owner", "Clan Owner")
            .User(MemberId, "member", "Member One");
        world.Clan(ClanId, "Simulated Clan", OwnerId)
            .TextChannel(GeneralId, "general")
            .VoiceChannel(VoiceId, VoiceLabel)
            .Role(RoleId, "Developer")
            .Member(MemberId, clanNick: "member-nick");
        return world;
    }
}
