using System.Reflection;
using System.Text.RegularExpressions;
using CsCheck;
using Monze.Application.Commands;
using Monze.Domain;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Ui;

/// <summary>
/// G23: MonzeMessages. Every catalog text is non-empty, has no raw id and only
/// documented placeholders; RateLimited never shows "0 giây" and rounds up;
/// ScheduleSaved names the kind in lower case and the time zone; help hints
/// use the configured command prefix and root.
/// </summary>
public sealed partial class G23MessageFormatterProperties
{
    [Fact]
    [Req("REQ-CMD-001")]
    [Covers("msg:TemporaryFailure")]
    public void Catalog_texts_have_no_raw_ids_or_undocumented_placeholders()
    {
        using var ledger = CaseLedger.Open("property", "G23");
        var failures = new List<string>();
        foreach (var field in typeof(MonzeMessages).GetFields(BindingFlags.Public | BindingFlags.Static).Where(static field => field.IsLiteral))
        {
            var text = (string)field.GetRawConstantValue()!;
            var placeholders = Placeholder().Matches(text).Select(static match => match.Value).Where(static value => value is not ("{user}" or "{role:Tên}" or "{channel:tên-kênh}")).ToList();
            var problem = string.IsNullOrWhiteSpace(text) ? "empty"
                : RawId().IsMatch(text) ? "raw id"
                : placeholders.Count > 0 ? $"placeholders {string.Join(",", placeholders)}"
                : null;
            if (problem is null)
            {
                ledger.Pass(field.Name);
            }
            else
            {
                ledger.Fail(field.Name, problem);
                failures.Add($"{field.Name}: {problem}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    [Req("REQ-RL-001")]
    [Covers("msg:RateLimited")]
    public void Rate_limit_text_rounds_up_to_whole_seconds()
    {
        PropertyRun.Run(
            "G23b",
            Gen.Long[-TimeSpan.TicksPerSecond, TimeSpan.TicksPerDay],
            static ticks =>
            {
                var retry = TimeSpan.FromTicks(ticks);
                var expected = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds));
                var text = MonzeMessages.RateLimited(retry);
                var tags = new Dictionary<string, string> { ["range"] = ticks <= TimeSpan.TicksPerSecond ? "sub-second" : ticks <= TimeSpan.TicksPerHour ? "hour" : "day" };
                return PropertyResult.Check(
                    text.Contains($" {expected} giây", StringComparison.Ordinal) && !text.Contains(" 0 giây", StringComparison.Ordinal),
                    retry.ToString(),
                    tags,
                    () => $"'{text}', expected {expected} seconds");
            },
            iterations: 20_000,
            declare: static ledger => ledger.Dimension("range", "sub-second", "hour", "day"));
    }

    [Fact]
    [Req("REQ-MTG-002", "REQ-HELP-001")]
    [Covers("msg:ScheduleSaved")]
    [Covers("msg:UnknownCommand")]
    public void Schedule_and_help_texts_follow_their_inputs()
    {
        var options = Gen.OneOf(
            Gen.Const(MonzeCommandOptions.Default),
            Gen.Select(Gen.OneOfConst("*", "!", "monze "), Gen.OneOfConst<string?>(null, "bot", "monze"), static (prefix, root) => new MonzeCommandOptions(prefix, root)));
        PropertyRun.Run(
            "G23c",
            Gen.Select(options, Gen.Enum<MeetingScheduleKind>(), Gen.Long[1, long.MaxValue], Gen.OneOfConst("Sprint", "Họp \"tuần\"", "🎯")),
            static value =>
            {
                var (commandOptions, kind, id, name) = value;
                var next = new DateTimeOffset(2026, 10, 8, 3, 0, 0, TimeSpan.Zero);
                var saved = MonzeMessages.ScheduleSaved(name, id, kind, next, "Asia/Ho_Chi_Minh");
                var unknown = MonzeMessages.UnknownCommand(commandOptions);
                var help = MonzeMessages.MonzeHelp(commandOptions, isAdmin: id % 2 == 0);
                var helpCommand = commandOptions.HasRoot ? $"{commandOptions.Prefix}{commandOptions.Root} help" : $"{commandOptions.Prefix}help";
                var tags = new Dictionary<string, string> { ["root"] = commandOptions.HasRoot ? "root" : "rootless", ["kind"] = kind.ToString() };
                var ok = saved.Contains($"kiểu {kind.ToString().ToLowerInvariant()}", StringComparison.Ordinal)
                    && saved.Contains($"\"{name}\"", StringComparison.Ordinal)
                    && saved.Contains("(Asia/Ho_Chi_Minh)", StringComparison.Ordinal)
                    && unknown.Contains(helpCommand, StringComparison.Ordinal)
                    && help.Contains(helpCommand, StringComparison.Ordinal);
                return PropertyResult.Check(ok, $"{kind} #{id} prefix='{commandOptions.Prefix}' root='{commandOptions.Root}'", tags, () => $"saved='{saved}' unknown='{unknown}' help='{help}'");
            },
            iterations: 10_000,
            declare: static ledger => ledger.Dimension("root", "root", "rootless").Dimension("kind", Enum.GetNames<MeetingScheduleKind>()));
    }

    [GeneratedRegex("\\{[^{}]*\\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("[0-9]{15,}")]
    private static partial Regex RawId();
}
