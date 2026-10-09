using System.Text.Json.Nodes;
using CsCheck;
using Mezon.Net.Client;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Monze.Ui;
using Xunit;

namespace Monze.Tests.Property.Ui;

/// <summary>
/// G21: MonzeMessageBuilder. Card picks its layout in a fixed priority (help
/// topic, welcome settings, welcome preview, schedules, welcome help, help
/// buttons, plain); every card has one embed, at most 25 fields, rows of at
/// most 5 components and unique component ids; the schedule list shows at
/// most 20 schedules with one cancel button each; summary actions keep at
/// most 25 fields.
/// G31: MessageContentReply sets the reply target without changing anything else.
/// </summary>
public sealed class G21G31MessageBuilderProperties
{
    private const string OutcomeTitle = "outcome-title";
    private static readonly string[] Branches = ["help", "welcome-settings", "welcome-preview", "schedules", "welcome-help", "help-buttons", "plain"];

    private static readonly Gen<CommandOutcome> Outcomes =
        from helpTopic in Gen.OneOfConst<string?>(null, null, null, "", "meeting", "welcome")
        from settings in Gen.Bool
        from preview in Gen.Bool
        from schedules in Gen.OneOf(Gen.Bool.Select(static _ => (int?)null), Gen.Int[0, 30].Select(static count => (int?)count))
        from welcomeHelp in Gen.Bool
        from helpButtons in Gen.Bool
        from fields in Gen.Int[0, 40]
        select new CommandOutcome
        {
            Title = OutcomeTitle,
            Text = "nội dung",
            HelpTopic = helpTopic,
            ShowWelcomeSettings = settings,
            WelcomeSettings = settings ? new WelcomeSettings(true, "Chào", 2) : null,
            ShowWelcomePreview = preview,
            WelcomeDraft = preview ? new WelcomeEmbedSettings(Title: "Xin chào") : null,
            ShowMeetingSchedules = schedules is not null,
            MeetingSchedules = schedules is { } count ? Schedules(count) : null,
            ShowWelcomeHelp = welcomeHelp,
            ShowHelpButtons = helpButtons,
            Fields = fields == 0 ? null : Enumerable.Range(0, fields).Select(static i => new CommandField($"F{i}", $"V{i}")).ToArray()
        };

    [Fact]
    [Req("REQ-CMD-001", "REQ-HELP-001", "REQ-MTG-001")]
    [Covers("btn:monze_meeting_now")]
    [Covers("btn:monze_help_close")]
    public void Cards_follow_layout_priority_and_size_limits()
    {
        PropertyRun.Run(
            "G21",
            Outcomes,
            static outcome =>
            {
                var expected = outcome.HelpTopic is not null ? "help"
                    : outcome.ShowWelcomeSettings ? "welcome-settings"
                    : outcome.ShowWelcomePreview && outcome.WelcomeDraft is not null ? "welcome-preview"
                    : outcome.ShowMeetingSchedules && outcome.MeetingSchedules is not null ? "schedules"
                    : outcome.ShowWelcomeHelp ? "welcome-help"
                    : outcome.ShowHelpButtons ? "help-buttons"
                    : "plain";
                var tags = new Dictionary<string, string> { ["branch"] = expected, ["fields"] = (outcome.Fields?.Count ?? 0) > 25 ? "over-25" : "within" };
                var content = MonzeMessageBuilder.Card(outcome, MonzeCommandOptions.Default);
                var input = $"{expected} fields={outcome.Fields?.Count ?? 0} schedules={outcome.MeetingSchedules?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}";
                var problem = Structure(content);
                if (problem is not null)
                {
                    return PropertyResult.Fail(input, tags, problem);
                }

                var ids = Ids(content);
                var title = content.Embeds![0].Title;
                var fieldCount = content.Embeds[0].Fields?.Count ?? 0;
                var correct = expected switch
                {
                    "help" => title != OutcomeTitle,
                    "welcome-settings" => ids.Contains(MonzeButtonId.WelcomeSave),
                    "welcome-preview" => ids.Contains(MonzeButtonId.WelcomeSettings) && !ids.Contains(MonzeButtonId.WelcomeStatus),
                    "schedules" => ids.Contains(MeetingButtonId.StartNow)
                        && ids.Count(static id => id.StartsWith(MeetingButtonId.CancelPrefix, StringComparison.Ordinal)) == Math.Min(20, outcome.MeetingSchedules!.Count)
                        && fieldCount == Math.Max(1, Math.Min(20, outcome.MeetingSchedules.Count)),
                    "welcome-help" => title == OutcomeTitle && ids.Count > 0 && !ids.Contains(MonzeButtonId.HelpMeeting) && fieldCount == Math.Max(1, Math.Min(25, outcome.Fields?.Count ?? 0)),
                    "help-buttons" => title == OutcomeTitle && ids.Contains(MonzeButtonId.HelpMeeting) && fieldCount == Math.Max(1, Math.Min(25, outcome.Fields?.Count ?? 0)),
                    _ => title == OutcomeTitle && ids.Count == 0 && fieldCount == Math.Max(1, Math.Min(25, outcome.Fields?.Count ?? 0))
                };
                return PropertyResult.Check(correct, input, tags, () => $"title='{title}' fields={fieldCount} ids=[{string.Join(",", ids)}]");
            },
            iterations: 30_000,
            declare: static ledger => ledger.Dimension("branch", Branches).Dimension("fields", "within", "over-25"));
    }

    [Fact]
    [Req("REQ-MTG-004")]
    public void Summary_action_items_keep_at_most_25_fields()
    {
        PropertyRun.Run(
            "G21b",
            Gen.Int[0, 40],
            static count =>
            {
                var presentation = new MeetingSummaryPresentation(
                    "Tóm tắt", "Việc cần làm", "Mô tả", "A, B", "Nội dung", "https://example.test/t",
                    Enumerable.Range(0, count).Select(static i => new MeetingSummaryActionItem($"P{i}", $"Việc {i}")).ToArray());
                var delivery = MonzeMessageBuilder.MeetingSummaryMessages(presentation, replyToMessageId: count % 2 == 0 ? 5 : null);
                var actions = MessageContent.Parse(delivery.ActionItemsContentJson);
                var summary = MessageContent.Parse(delivery.SummaryContentJson);
                var fields = actions.Embeds?[0].Fields?.Count ?? 0;
                var tags = new Dictionary<string, string> { ["items"] = count > 25 ? "over-25" : "within" };
                return PropertyResult.Check(
                    fields == Math.Min(25, count) && (summary.ReplyToMessageId == (count % 2 == 0 ? 5 : null)),
                    $"{count} action items",
                    tags,
                    () => $"{fields} fields, reply {summary.ReplyToMessageId}");
            },
            iterations: 2_000,
            declare: static ledger => ledger.Dimension("items", "within", "over-25"));
    }

    [Fact]
    [Req("REQ-OUT-001", "REQ-MTG-001")]
    public void Reply_target_is_set_without_changing_the_rest()
    {
        var contents = Gen.OneOf(
            Gen.Const(MonzeMessageBuilder.Card("Tiêu đề", "Nội dung", MonzeTone.Info)),
            Gen.Const(MonzeMessageBuilder.MeetingSummary("Tóm tắt", 9)),
            Gen.Const(MonzeMessageBuilder.MeetingSchedules(Schedules(3), MonzeCommandOptions.Default)));
        PropertyRun.Run(
            "G31",
            Gen.Select(contents, Gen.OneOf(Gen.Long[-5, 0], Gen.Const(9L), Gen.Long[1, long.MaxValue])),
            static value =>
            {
                var (content, reply) = value;
                var result = MessageContentReply.Apply(content, reply);
                var tags = new Dictionary<string, string> { ["reply"] = reply <= 0 ? "none" : reply == content.ReplyToMessageId ? "same" : "new" };
                if (reply <= 0 || reply == content.ReplyToMessageId)
                {
                    return PropertyResult.Check(ReferenceEquals(result, content), $"reply={reply}", tags, static () => "content was rebuilt for a no-op");
                }

                var before = (JsonObject)JsonNode.Parse(content.ToJson())!;
                var after = (JsonObject)JsonNode.Parse(result.ToJson())!;
                before.Remove("rpl");
                after.Remove("rpl");
                return PropertyResult.Check(
                    result.ReplyToMessageId == reply && JsonNode.DeepEquals(before, after),
                    $"reply={reply}",
                    tags,
                    () => $"reply {result.ReplyToMessageId}, other fields changed: {JsonNode.DeepEquals(before, after) is false}");
            },
            iterations: 5_000,
            declare: static ledger => ledger.Dimension("reply", "none", "same", "new"));
    }

    private static MeetingScheduleSummary[] Schedules(int count)
        => Enumerable.Range(1, count)
            .Select(static i => new MeetingScheduleSummary(i, i % 3 == 0 ? "   " : $"Lịch {i} " + new string('x', i * 5), MeetingScheduleKind.Weekly, new DateTimeOffset(2026, 10, 9, 3, 0, 0, TimeSpan.Zero), "Asia/Ho_Chi_Minh", 7))
            .ToArray();

    private static string? Structure(MessageContent content)
    {
        if (content.Embeds is not { Count: 1 })
        {
            return $"{content.Embeds?.Count ?? 0} embeds";
        }

        if ((content.Embeds[0].Fields?.Count ?? 0) > 25)
        {
            return $"{content.Embeds[0].Fields!.Count} fields";
        }

        var rows = content.Components ?? [];
        if (rows.Any(static row => row.Components.Count > 5))
        {
            return "a row has more than 5 components";
        }

        var ids = Ids(content);
        return ids.Count == ids.Distinct(StringComparer.Ordinal).Count() ? null : "duplicate component ids";
    }

    private static List<string> Ids(MessageContent content)
    {
        var ids = (content.Components ?? []).SelectMany(static row => row.Components).Select(static component => component.Id).ToList();
        foreach (var field in content.Embeds?.SelectMany(static embed => embed.Fields ?? []) ?? [])
        {
            if (field.Input is { } input)
            {
                ids.Add(input.Id);
            }
        }

        return ids;
    }
}
