using System.Globalization;
using System.Text.Json.Nodes;
using CsCheck;
using Monze.Domain;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Monze.Ui;
using Xunit;

namespace Monze.Tests.Property.Ui;

/// <summary>
/// G04: MeetingScheduleFormParser over every way a client can encode a form
/// field (direct value, plain string, "-component" suffix, component object
/// in an array, nested container, "values" array). The parser must return
/// the trimmed name (1–120 characters), the payload's calendar date as
/// dd/MM/yyyy, an HH:mm or H:mm time and the frequency; DescribeInput must
/// never echo a field value (privacy).
/// Known gap CAND-12: an ISO timestamp with an offset is converted to the
/// server's local time zone before the date is taken, so the calendar date
/// can move to the adjacent day.
/// </summary>
public sealed class G04MeetingScheduleFormProperties
{
    private static readonly string[] Encodings = ["direct", "string", "suffix", "component", "nested", "values", "missing"];

    private static readonly Gen<string> Names = Gen.OneOf(
        Gen.OneOfConst("Sprint Review", "  Họp tuần 🎯  ", "", "   ", "A", "Standup\tdaily"),
        Gen.Int[118, 122].Select(static length => new string('n', length)));

    private static readonly Gen<(string Value, string Shape)> Dates = Gen.OneOf(
        Gen.Select(Gen.Int[2026, 2030], Gen.Int[1, 12], Gen.Int[1, 28], static (y, m, d) => ($"{d:00}/{m:00}/{y}", "local")),
        Gen.Select(Gen.Int[2026, 2030], Gen.Int[1, 12], Gen.Int[1, 28], static (y, m, d) => ($"{y}-{m:00}-{d:00}", "iso")),
        Gen.Select(Gen.Int[2026, 2030], Gen.Int[1, 12], Gen.Int[1, 28], Gen.Int[0, 23], static (y, m, d, h) => ($"{y}-{m:00}-{d:00}T{h:00}:30:00", "iso-time")),
        Gen.Select(
            Gen.Int[2026, 2030], Gen.Int[1, 12], Gen.Int[1, 28], Gen.OneOfConst(0, 1, 3, 21, 23),
            Gen.OneOfConst("Z", "+07:00", "-05:00", "+14:00", "-12:00"),
            static (y, m, d, h, offset) => ($"{y}-{m:00}-{d:00}T{h:00}:30:00{offset}", "iso-offset")),
        Gen.Select(Gen.Int[2026, 2030], Gen.Int[1, 12], Gen.Int[1, 28], static (y, m, d) => ($"{y}-{m:00}-{d:00}T23:59:59.999Z", "iso-millis")),
        Gen.OneOfConst("31/02/2026", "1/10/2026", "2026/10/01", "", " ").Select(static value => (value, "invalid")));

    private static readonly Gen<string> Times = Gen.OneOfConst("18:30", "09:05", "9:05", " 18:30 ", "24:00", "18:30:00", "1830", "", "٠٩:٠٠");

    private static readonly Gen<string> Frequencies = Gen.OneOfConst("once", "daily", "weekly", "Daily", "WEEKLY", "", "monthly");

    private static readonly Gen<FormCase> Cases =
        from name in Names
        from nameEncoding in Gen.OneOfConst(Encodings)
        from date in Dates
        from dateEncoding in Gen.OneOfConst(Encodings)
        from time in Times
        from timeEncoding in Gen.OneOfConst(Encodings)
        from frequency in Frequencies
        from frequencyEncoding in Gen.OneOfConst(Encodings)
        select new FormCase(
            new Field(MeetingButtonId.ScheduleName, name, nameEncoding),
            new Field(MeetingButtonId.ScheduleDate, date.Value, dateEncoding),
            date.Shape,
            new Field(MeetingButtonId.ScheduleTime, time, timeEncoding),
            new Field(MeetingButtonId.ScheduleFrequency, frequency, frequencyEncoding));

    [Fact]
    [Req("REQ-MTG-001", "REQ-MTG-002")]
    [Covers("btn:monze_meeting_schedule_submit")]
    public void Schedule_form_reads_every_field_encoding()
    {
        PropertyRun.Run(
            "G04",
            Cases,
            static item =>
            {
                var tags = new Dictionary<string, string>
                {
                    ["name"] = item.Name.Encoding,
                    ["date"] = item.Date.Encoding,
                    ["date-shape"] = item.DateShape,
                    ["time"] = item.Time.Encoding
                };
                var extraData = item.ToJson();
                var input = extraData.Length <= 200 ? extraData : extraData[..200] + "…";
                var ok = MeetingScheduleFormParser.TryRead(extraData, out var name, out var date, out var time, out var kind, out _);
                var description = MeetingScheduleFormParser.DescribeInput(extraData);

                var expectedName = item.Name.Read()?.Trim() ?? string.Empty;
                var expectedTime = item.Time.Read()?.Trim() ?? string.Empty;
                var rawDate = item.Date.Read();
                var expectedDate = ExpectedDate(rawDate);
                var expectedKind = item.Frequency.Read()?.ToLowerInvariant() switch
                {
                    "daily" => MeetingScheduleKind.Daily,
                    "weekly" => MeetingScheduleKind.Weekly,
                    _ => MeetingScheduleKind.Once
                };
                var expectedOk = expectedName.Length is >= 1 and <= 120
                    && IsLocalDate(expectedDate)
                    && TimeOnly.TryParseExact(expectedTime, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

                var leaked = new[] { item.Name, item.Date, item.Time }
                    .Select(static field => field.Read()?.Trim())
                    .FirstOrDefault(value => value is { Length: >= 3 } && description.Contains(value, StringComparison.Ordinal));
                if (leaked is not null)
                {
                    return PropertyResult.Fail(input, tags, $"DescribeInput echoed a field value: {description}");
                }

                var sameFields = name == expectedName && time == expectedTime && kind == expectedKind;
                var sameDate = date == expectedDate;
                var note = $"expected ok={expectedOk} date={expectedDate}, got ok={ok} date={date} name='{name}' time='{time}' kind={kind}";
                if (item.DateShape == "iso-offset" && rawDate is not null && item.Date.Encoding != "missing")
                {
                    var consistent = sameFields && sameDate && ok == expectedOk;
                    return consistent
                        ? PropertyResult.Pass(input, tags, "CAND-12")
                        : sameFields && !sameDate ? PropertyResult.Known("CAND-12", input, tags, note) : PropertyResult.Fail(input, tags, note);
                }

                return PropertyResult.Check(sameFields && sameDate && ok == expectedOk, input, tags, () => note);
            },
            iterations: 60_000,
            declare: static ledger => ledger
                .Dimension("name", Encodings)
                .Dimension("date", Encodings)
                .Dimension("time", Encodings)
                .Dimension("date-shape", "local", "iso", "iso-time", "iso-offset", "iso-millis", "invalid"),
            knownDefects: ["CAND-12"]);
    }

    /// <summary>The calendar date written in the payload, as dd/MM/yyyy; other text is kept trimmed.</summary>
    private static string ExpectedDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim();
        if (IsLocalDate(text))
        {
            return text;
        }

        return text.Length >= 10
            && DateTime.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && (text.Length == 10 || text[10] == 'T')
                ? date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                : text;
    }

    private static bool IsLocalDate(string value)
        => DateTime.TryParseExact(value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public sealed record Field(string Id, string Value, string Encoding)
    {
        /// <summary>The value a reader of this encoding sees (a "values" array skips blank entries).</summary>
        public string? Read()
            => Encoding switch
            {
                "missing" => null,
                "values" => string.IsNullOrWhiteSpace(Value) ? null : Value,
                _ => Value
            };

        public void WriteTo(JsonObject root, JsonArray components)
        {
            switch (Encoding)
            {
                case "direct":
                    root[Id] = new JsonObject { ["value"] = Value };
                    break;
                case "string":
                    root[Id] = Value;
                    break;
                case "suffix":
                    root[Id + "-component"] = new JsonObject { ["value"] = Value };
                    break;
                case "component":
                    components.Add(new JsonObject { ["id"] = Id, ["value"] = Value });
                    break;
                case "nested":
                    root["form"] ??= new JsonObject { ["sections"] = new JsonArray() };
                    ((JsonArray)root["form"]!["sections"]!).Add(new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["id"] = Id, ["value"] = Value }) });
                    break;
                case "values":
                    root[Id] = new JsonObject { ["values"] = new JsonArray(JsonValue.Create(Value)) };
                    break;
            }
        }
    }

    public sealed record FormCase(Field Name, Field Date, string DateShape, Field Time, Field Frequency)
    {
        public string ToJson()
        {
            var root = new JsonObject();
            var components = new JsonArray();
            foreach (var field in new[] { Name, Date, Time, Frequency })
            {
                field.WriteTo(root, components);
            }

            if (components.Count > 0)
            {
                root["components"] = components;
            }

            return root.ToJsonString();
        }
    }
}
