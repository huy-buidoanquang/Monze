using System.Text.RegularExpressions;
using CsCheck;
using Mezon.Net.Client;
using Monze.Application;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Ui;

/// <summary>
/// G20: MeetingSummaryComposer. Participants and action-item owners are
/// shown by profile label (clan nick, display name, user name) or as
/// "Thành viên N", never as a raw id; action items keep at most 25 fields;
/// the summary replies to the source message only when the notification went
/// to the meeting's text channel.
/// </summary>
public sealed partial class G20MeetingSummaryComposerProperties
{
    private const long Clan = 2_104_288_434_238_525_000;
    private static readonly long[] Users = [2_104_288_434_238_525_100, 2_104_288_434_238_525_101, 2_104_288_434_238_525_102, 2_104_288_434_238_525_103];

    private static readonly Gen<string> Identity = Gen.OneOf(
        Gen.OneOfConst(Users).Select(static id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Gen.OneOfConst("guest-1", "Bảo", "0", "-5"));

    [Fact]
    [Req("REQ-MTG-004")]
    [Covers("port:IUserProfileRepository.GetByIdAsync")]
    public void Summaries_show_labels_not_ids_and_reply_to_the_right_message()
    {
        var cases =
            from participants in Identity.Array[0, 6]
            from owners in Identity.Array[0, 30]
            from profileMask in Gen.Int[0, 15]
            from sameChannel in Gen.Bool
            from source in Gen.OneOf(Gen.Bool.Select(static _ => (long?)null), Gen.Long[1, 1_000].Select(static id => (long?)id))
            from notification in Gen.OneOf(Gen.Bool.Select(static _ => (long?)null), Gen.Long[1, 1_000].Select(static id => (long?)id))
            select (participants, owners, profileMask, sameChannel, source, notification);
        PropertyRun.Run(
            "G20",
            cases,
            static value =>
            {
                var (participants, owners, profileMask, sameChannel, source, notification) = value;
                var profiles = new Profiles(profileMask);
                var composer = new Monze.MeetingSummaryComposer(profiles);
                var result = new AgentSummaryResult(
                    "room-abc",
                    "Đã thống nhất kế hoạch.",
                    "toàn văn",
                    new DateTimeOffset(2026, 10, 8, 2, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 10, 8, 2, 30, 0, TimeSpan.Zero),
                    participants,
                    participants.Select(static (participant, i) => new AgentSpeechDuration(participant, 60 * (i + 1))).ToArray(),
                    owners.Select(static (owner, i) => new AgentActionItemGroup(owner, [$"Việc {i}"])).ToArray(),
                    "{}");
                var context = new MeetingSummaryContext(
                    1, Clan, 2_104_288_434_238_525_200, 2_104_288_434_238_525_201, "room-abc", "Họp tuần", "Phòng 1",
                    null, null, notification, sameChannel ? 2_104_288_434_238_525_201 : 2_104_288_434_238_525_202, source);
                var delivery = composer.ComposeAsync(result, context, CancellationToken.None).GetAwaiter().GetResult();
                var tags = new Dictionary<string, string>
                {
                    ["channel"] = sameChannel ? "same" : "other",
                    ["actions"] = owners.Length > 25 ? "over-25" : owners.Length == 0 ? "none" : "within"
                };
                if (delivery is null)
                {
                    return PropertyResult.Fail("compose", tags, "no delivery");
                }

                var summary = MessageContent.Parse(delivery.SummaryContentJson);
                var actions = MessageContent.Parse(delivery.ActionItemsContentJson);
                var visible = Visible(summary) + "\n" + Visible(actions);
                var expectedReply = sameChannel ? source ?? notification : null;
                var actionFields = actions.Embeds?[0].Fields?.Count ?? 0;
                var problem = RawId().IsMatch(visible) ? "raw id in the summary"
                    : summary.ReplyToMessageId != expectedReply ? $"reply {summary.ReplyToMessageId}, expected {expectedReply}"
                    : actionFields > 25 ? $"{actionFields} action fields"
                    : profiles.Labels.Any(label => participants.Any(participant => participant == label.Key) && !visible.Contains(label.Value, StringComparison.Ordinal)) ? "a known participant's label is missing"
                    : null;
                return problem is null
                    ? PropertyResult.Pass($"{participants.Length} participants, {owners.Length} owners", tags)
                    : PropertyResult.Fail($"{participants.Length} participants, {owners.Length} owners", tags, problem);
            },
            iterations: 10_000,
            declare: static ledger => ledger.Dimension("channel", "same", "other").Dimension("actions", "none", "within", "over-25"));
    }

    private static string Visible(MessageContent content)
        => string.Join("\n", (content.Embeds ?? []).SelectMany(static embed =>
            new[] { embed.Title, embed.Description }.Concat((embed.Fields ?? []).SelectMany(static field => new[] { field.Name, field.Value }))));

    [GeneratedRegex("[0-9]{15,}")]
    private static partial Regex RawId();

    /// <summary>Profiles for the first users; the mask picks which of them have a profile, and which kind of name.</summary>
    private sealed class Profiles(int mask) : IUserProfileRepository
    {
        public Dictionary<string, string> Labels { get; } = Users
            .Where((_, i) => (mask & (1 << i)) != 0)
            .ToDictionary(
                static id => id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                static id => (id % 3) switch { 0 => "Nick " + (id % 100), 1 => "Hiển thị " + (id % 100), _ => "user" + (id % 100) });

        public Task<UserProfileSnapshot?> GetByIdAsync(long clanId, long userId, CancellationToken cancellationToken)
        {
            var key = userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (clanId != Clan || !Labels.TryGetValue(key, out var label))
            {
                return Task.FromResult<UserProfileSnapshot?>(null);
            }

            var kind = userId % 3;
            return Task.FromResult<UserProfileSnapshot?>(new UserProfileSnapshot(
                clanId,
                userId,
                kind == 0 ? label : "  ",
                kind == 1 ? label : null,
                kind == 2 ? label : null,
                null,
                DateTimeOffset.UnixEpoch));
        }

        public Task UpsertAsync(
            long clanId,
            long userId,
            string? clanNick,
            string? displayName,
            string? username,
            string? avatarUrl,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
