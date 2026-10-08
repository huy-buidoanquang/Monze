using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Areas;

/// <summary>
/// Avatar: the caller's own avatar, a member's by username, "not found" for
/// someone outside the clan (in both directions) and "no avatar" for a member
/// without one; profiles are cached per clan only.
/// </summary>
public sealed class AvatarAreaTests
{
    [DbFact]
    [Req("REQ-AVA-001")]
    [Covers("cmd:avatar", "cmd:ava", "cmd:avt", "msg:AvatarNotFound", "msg:AvatarUnavailable")]
    public async Task Avatar_shows_members_of_the_callers_clan_only()
    {
        await using var host = await E2EActions.StartAsync("avatar");
        var mark = await E2EOracles.MarkAsync(host);

        var self = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*avatar");
        var byName = await E2EActions.CommandAsync(host, GeneralId, Member2Id, "*ava member");
        var outsider = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*avatar outsider");
        var noAvatar = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*avt member2");
        var fromOtherClan = await E2EActions.CommandAsync(host, OtherGeneralId, OutsiderId, "*avatar member", clanId: OtherClanId);

        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of(
            (self, ResponseKind.Reply),
            (byName, ResponseKind.Reply),
            (outsider, ResponseKind.Reply),
            (noAvatar, ResponseKind.Reply),
            (fromOtherClan, ResponseKind.Reply)));
        foreach (var command in new[] { self, byName })
        {
            var card = E2EContent.Parse(E2EActions.NewMessageAfter(host, command, GeneralId));
            var embed = Assert.Single(card.Embeds!);
            Assert.Equal("Avatar", embed.Title);
            Assert.Equal(MemberNick, Assert.Single(embed.Fields!).Value);
            Assert.Equal(MemberAvatar, embed.Image?.Url);
        }

        Assert.Contains(MonzeMessages.AvatarNotFound, E2EContent.Visible(E2EActions.NewMessageAfter(host, outsider, GeneralId)));
        Assert.Contains(MonzeMessages.AvatarUnavailable, E2EContent.Visible(E2EActions.NewMessageAfter(host, noAvatar, GeneralId)));
        Assert.Contains(MonzeMessages.AvatarNotFound, E2EContent.Visible(E2EActions.NewMessageAfter(host, fromOtherClan, OtherGeneralId)));
        var profiles = await host.RowsAsync("SELECT clan_id, user_id FROM clan_user_profile WHERE user_id IN (@member, @outsider) ORDER BY clan_id, user_id;",
            ("member", MemberId),
            ("outsider", OutsiderId));
        Assert.DoesNotContain(profiles, static row => (long)row[0]! == ClanId && (long)row[1]! == OutsiderId);
        Assert.DoesNotContain(profiles, static row => (long)row[0]! == OtherClanId && (long)row[1]! == MemberId);
        Assert.Contains(profiles, static row => (long)row[0]! == ClanId && (long)row[1]! == MemberId);
    }
}
