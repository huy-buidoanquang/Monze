using System.Globalization;
using Monze.Domain;

namespace Monze.Tests.Property.Domain;

/// <summary>
/// Reference for the meeting command grammar, written from the documented
/// forms (help catalog and the command table of docs/monze-browser-test-plan.md)
/// rather than from MeetingCommandParser:
///   now
///   cancel &lt;id&gt;                         id: ASCII digits, value 1..long.MaxValue
///   &lt;name&gt; dd/MM/yyyy HH:mm [kind]          H:mm too, as in the daily form
///   &lt;name&gt; &lt;kind&gt; dd/MM/yyyy HH:mm         one frequency at most
///   &lt;kind&gt; &lt;rest&gt;                        legacy, validated later by the calculator
/// The name is 1..120 UTF-16 characters; keywords are ASCII, case-insensitive.
/// </summary>
internal static class ReferenceMeetingCommandParser
{
    public static MeetingRequest? Parse(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return null;
        }

        if (Is(tokens[0], "now"))
        {
            return tokens.Count == 1 ? new MeetingRequest(MeetingScheduleKind.Now, null) : null;
        }

        if (Is(tokens[0], "cancel"))
        {
            return tokens.Count == 2 && IsPositiveId(tokens[1], out var id)
                ? new MeetingRequest(MeetingScheduleKind.Once, null, null, id)
                : null;
        }

        if (Named(tokens) is { } named)
        {
            return named;
        }

        return Kind(tokens[0]) is { } legacy && tokens.Count >= 2
            ? new MeetingRequest(legacy, null, string.Join(' ', tokens.Skip(1)), null)
            : null;
    }

    public static bool IsDate(string value)
        => value.Length == 10
            && value[2] == '/'
            && value[5] == '/'
            && Digits(value, 0, 2, out var day)
            && Digits(value, 3, 2, out var month)
            && Digits(value, 6, 4, out var year)
            && year >= 1
            && month is >= 1 and <= 12
            && day >= 1
            && day <= DateTime.DaysInMonth(year, month);

    public static bool IsTime(string value)
    {
        var colon = value.Length - 3;
        return colon is 1 or 2
            && value[colon] == ':'
            && Digits(value, 0, colon, out var hour)
            && Digits(value, colon + 1, 2, out var minute)
            && hour <= 23
            && minute <= 59;
    }

    public static MeetingScheduleKind? Kind(string token)
        => Is(token, "once") ? MeetingScheduleKind.Once
            : Is(token, "daily") ? MeetingScheduleKind.Daily
            : Is(token, "weekly") ? MeetingScheduleKind.Weekly
            : null;

    private static MeetingRequest? Named(IReadOnlyList<string> tokens)
    {
        var date = -1;
        for (var i = 1; i < tokens.Count; i++)
        {
            if (IsDate(tokens[i]))
            {
                date = i;
                break;
            }
        }

        if (date < 1 || date + 1 >= tokens.Count || !IsTime(tokens[date + 1]))
        {
            return null;
        }

        // "<kind> dd/MM/yyyy HH:mm" has no name: it is the legacy form.
        if (date == 1 && tokens.Count == 3 && Kind(tokens[0]) is not null)
        {
            return null;
        }

        var nameEnd = date;
        MeetingScheduleKind? before = null;
        if (date > 1 && Kind(tokens[date - 1]) is { } kindBefore)
        {
            before = kindBefore;
            nameEnd--;
        }

        MeetingScheduleKind? after = null;
        var rest = tokens.Count - (date + 2);
        if (rest == 1 && Kind(tokens[date + 2]) is { } kindAfter)
        {
            after = kindAfter;
        }
        else if (rest != 0)
        {
            return null;
        }

        if (before is not null && after is not null)
        {
            return null;
        }

        var name = string.Join(' ', tokens.Take(nameEnd));
        if (name.Length is 0 or > 120)
        {
            return null;
        }

        return new MeetingRequest(before ?? after ?? MeetingScheduleKind.Once, name, $"{tokens[date]} {tokens[date + 1]}", null);
    }

    private static bool IsPositiveId(string value, out long id)
    {
        id = 0;
        if (value.Length == 0 || value.Any(static ch => ch is < '0' or > '9'))
        {
            return false;
        }

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    private static bool Is(string token, string keyword)
        => string.Equals(token, keyword, StringComparison.OrdinalIgnoreCase);

    private static bool Digits(string value, int start, int length, out int number)
    {
        number = 0;
        for (var i = start; i < start + length; i++)
        {
            if (value[i] is < '0' or > '9')
            {
                return false;
            }

            number = (number * 10) + (value[i] - '0');
        }

        return true;
    }
}
