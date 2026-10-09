using CsCheck;
using Monze.Domain;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Domain;

/// <summary>
/// G01: MeetingCommandParser against the documented grammar
/// (ReferenceMeetingCommandParser), over 18 command shapes, keyword casing
/// and host culture. Regressions: CAND-03 (a signed id, "cancel +5", was
/// accepted), CAND-08 (two frequencies were accepted, the last one kept) and,
/// in G01b, CAND-02 (the named form rejected H:mm times the daily form takes).
/// </summary>
public sealed class G01MeetingCommandParserProperties
{
    private static readonly string[] Shapes =
    [
        "empty", "now", "now-extra", "cancel-valid", "cancel-signed", "cancel-invalid", "cancel-arity",
        "named", "named-kind-before", "named-kind-after", "named-two-kinds", "named-bad-date", "named-bad-time",
        "named-long-name", "named-trailing-junk", "legacy-daily", "legacy-weekly", "legacy-once"
    ];

    private static readonly string[] Casings = ["lower", "upper", "mixed"];

    private static readonly Gen<Token> Word = Gen.OneOfConst(
        "Sprint", "Review", "Họp", "Nhóm", "kế", "hoạch", "Q4", "sync", "🎯", "Đánh", "giá", "Tuần", "Standup",
        "A", "x1", "Ôn", "İstanbul", "Ⅻ", "tổng-kết", "#7").Select(static text => new Token(text, false));

    private static readonly Gen<Token[]> Name = Word.Array[1, 4];

    private static readonly Gen<Token> ValidDate =
        from year in Gen.Int[2000, 2099]
        from month in Gen.Int[1, 12]
        from day in Gen.Int[1, DateTime.DaysInMonth(year, month)]
        select new Token($"{day:00}/{month:00}/{year:0000}", false);

    private static readonly Gen<Token> BadDate = Gen.OneOfConst(
        "31/02/2026", "1/2/2026", "2026-10-01", "00/01/2026", "32/01/2026", "29/02/2027", "٠١/٠٢/٢٠٢٦",
        "01/13/2026", "01/01/10000", "01-01-2026", "01/01/0000").Select(static text => new Token(text, false));

    private static readonly Gen<Token> ValidTime =
        from hour in Gen.Int[0, 23]
        from minute in Gen.Int[0, 59]
        select new Token($"{hour:00}:{minute:00}", false);

    private static readonly Gen<Token> BadTime = Gen.OneOfConst(
        "24:00", "9:5", "12:60", "1200", "12:5", "٠٩:٠٠", "25:61", "-1:00", "12:00:00").Select(static text => new Token(text, false));

    private static readonly Gen<Token> KindWord = Gen.OneOfConst("once", "daily", "weekly").Select(static text => new Token(text, true));

    private static readonly Gen<string> Id = Gen.Frequency(
        (3, Gen.Long[1, 1_000].Select(static id => id.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        (1, Gen.Long[1, long.MaxValue].Select(static id => id.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        (1, Gen.Long[1, 999].Select(static id => "00" + id.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    private static readonly Gen<string> InvalidId = Gen.OneOf(
        Gen.OneOfConst("0", "abc", "99999999999999999999", "1e3", "١٢", "", "5x", "0x1F"),
        Gen.Long[1, 10_000].Select(static id => "-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static readonly Gen<Token> WeeklyDay = Gen.OneOfConst(
        "0", "1", "6", "t2", "t7", "cn", "mon", "sunday", "9", "T3").Select(static text => new Token(text, false));

    private static readonly Gen<(string Shape, Token[] Tokens)> Shape = Gen.OneOf(
        Gen.Const(("empty", Array.Empty<Token>())),
        Gen.Const(("now", new[] { Keyword("now") })),
        Word.Select(static extra => ("now-extra", new[] { Keyword("now"), extra })),
        Id.Select(static id => ("cancel-valid", new[] { Keyword("cancel"), new Token(id, false) })),
        Id.Select(static id => ("cancel-signed", new[] { Keyword("cancel"), new Token("+" + id, false) })),
        InvalidId.Select(static id => ("cancel-invalid", new[] { Keyword("cancel"), new Token(id, false) })),
        Gen.OneOf(
            Gen.Const(new[] { Keyword("cancel") }),
            Id.Select(static id => new[] { Keyword("cancel"), new Token(id, false), new Token("now", false) }))
            .Select(static tokens => ("cancel-arity", tokens)),
        Gen.Select(Name, ValidDate, ValidTime, static (name, date, time) => ("named", Concat(name, date, time))),
        Gen.Select(Name, KindWord, ValidDate, ValidTime, static (name, kind, date, time) => ("named-kind-before", Concat(name, kind, date, time))),
        Gen.Select(Name, ValidDate, ValidTime, KindWord, static (name, date, time, kind) => ("named-kind-after", Concat(name, date, time, kind))),
        Gen.Select(Name, KindWord, ValidDate, ValidTime, KindWord, static (name, first, date, time, last) => ("named-two-kinds", Concat(name, first, date, time, last))),
        Gen.Select(Name, BadDate, ValidTime, static (name, date, time) => ("named-bad-date", Concat(name, date, time))),
        Gen.Select(Name, ValidDate, BadTime, static (name, date, time) => ("named-bad-time", Concat(name, date, time))),
        Gen.Select(Gen.Int[115, 130], ValidDate, ValidTime, static (length, date, time) => ("named-long-name", new[] { new Token(new string('n', length), false), date, time })),
        Gen.Select(Name, ValidDate, ValidTime, Word, static (name, date, time, junk) => ("named-trailing-junk", Concat(name, date, time, junk))),
        Gen.OneOf(ValidTime, BadTime, WeeklyDay).Select(static when => ("legacy-daily", new[] { Keyword("daily"), when })),
        Gen.Select(WeeklyDay, Gen.OneOf(ValidTime, BadTime), static (day, time) => ("legacy-weekly", new[] { Keyword("weekly"), day, time })),
        Gen.Select(ValidDate, ValidTime, static (date, time) => ("legacy-once", new[] { Keyword("once"), date, time })));

    private static readonly Gen<CommandCase> Commands =
        Gen.Select(Shape, Gen.OneOfConst(Casings), Cultures.Gen, static (shape, casing, culture) =>
            new CommandCase(shape.Shape, casing, culture, shape.Tokens.Select(token => token.Keyword ? Case(token.Text, casing) : token.Text).ToArray()));

    [Fact]
    [Req("REQ-MTG-001")]
    [Covers("cmd:meeting")]
    public void Parser_matches_the_documented_grammar()
    {
        PropertyRun.Run(
            "G01",
            Commands,
            static command =>
            {
                var tags = new Dictionary<string, string>
                {
                    ["shape"] = command.Shape,
                    ["case"] = command.Casing,
                    ["culture"] = command.Culture
                };
                var input = Describe(command.Tokens);
                var expected = ReferenceMeetingCommandParser.Parse(command.Tokens);
                var actual = Cultures.Run(command.Culture, () => MeetingCommandParser.TryParse(command.Tokens, out var request) ? request : null);
                var same = Equals(expected, actual);
                var note = $"expected {Show(expected)}, got {Show(actual)}";
                return PropertyResult.Check(same, input, tags, () => note);
            },
            iterations: 150_000,
            declare: static ledger => ledger
                .Dimension("shape", Shapes)
                .Dimension("case", Casings)
                .Dimension("culture", Cultures.Names),
            print: static command => Describe(command.Tokens));
    }

    [Fact]
    [Req("REQ-MTG-001", "REQ-MTG-002")]
    public void Named_and_daily_forms_accept_the_same_time_formats()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var times = Gen.OneOf(
            ValidTime.Select(static token => ("HH:mm", token.Text)),
            Gen.Select(Gen.Int[0, 9], Gen.Int[0, 59], static (hour, minute) => ("H:mm", $"{hour}:{minute:00}")),
            Gen.OneOfConst("24:00", "12:60", "1200", "12:5", "٠٩:٠٠", "25:61", "-1:00", "12:00:00", "9:5", "009:00").Select(static text => ("invalid", text)));
        PropertyRun.Run(
            "G01b",
            Gen.Select(times, Cultures.Gen),
            value =>
            {
                var ((form, time), culture) = value;
                var tags = new Dictionary<string, string> { ["form"] = form, ["culture"] = culture };
                var named = Cultures.Run(culture, () => MeetingCommandParser.TryParse(["Sync", "01/01/2030", time], out _));
                var daily = Cultures.Run(culture, () => MeetingScheduleCalculator.TryGetNext(
                    MeetingScheduleKind.Daily, time, "Asia/Ho_Chi_Minh", now, out _, out _));
                var note = $"named form {(named ? "accepts" : "rejects")} '{time}', daily form {(daily ? "accepts" : "rejects")} it";
                return PropertyResult.Check(named == daily, time, tags, () => note);
            },
            iterations: 20_000,
            declare: static ledger => ledger.Dimension("form", "HH:mm", "H:mm", "invalid").Dimension("culture", Cultures.Names));
    }

    private static Token Keyword(string text) => new(text, true);

    private static Token[] Concat(Token[] head, params Token[] tail) => [.. head, .. tail];

    private static string Case(string keyword, string casing)
        => casing switch
        {
            "upper" => keyword.ToUpperInvariant(),
            "mixed" => char.ToUpperInvariant(keyword[0]) + keyword[1..],
            _ => keyword
        };

    private static string Describe(string[] tokens)
        => "[" + string.Join(" ", tokens.Select(static token => token.Length > 24 ? $"{token[..8]}…({token.Length})" : token)) + "]";

    private static string Show(MeetingRequest? request)
        => request is null
            ? "reject"
            : $"{request.Kind} name={request.Name ?? "-"} when={request.WhenText ?? "-"} cancel={request.CancelScheduleId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}";

    private sealed record Token(string Text, bool Keyword);

    public sealed record CommandCase(string Shape, string Casing, string Culture, string[] Tokens);
}
