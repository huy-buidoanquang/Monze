using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Monze.Tests.Property.Domain;

/// <summary>
/// Time zones with unusual rules and their offset transitions in 2010–2030,
/// found by an hourly scan refined to the minute, plus an independent
/// local-to-UTC conversion with the documented ambiguity rule (an ambiguous
/// local time uses the earlier instant, i.e. the larger offset).
/// </summary>
internal static partial class TimeZoneEdges
{
    public static readonly string[] Zones =
    [
        "Asia/Ho_Chi_Minh", "Etc/UTC", "America/New_York", "Europe/London", "America/Sao_Paulo",
        "Australia/Lord_Howe", "Pacific/Chatham", "Pacific/Apia", "America/St_Johns", "Asia/Kathmandu"
    ];

    public static readonly DateTimeOffset RangeStart = new(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset RangeEnd = new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly ConcurrentDictionary<string, DateTimeOffset[]> Transitions = new(StringComparer.Ordinal);

    public static TimeZoneInfo Find(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    public static IReadOnlyList<DateTimeOffset> TransitionsOf(string id) => Transitions.GetOrAdd(id, Scan);

    public static bool HasTransitions(string id) => TransitionsOf(id).Count > 0;

    /// <summary>Local wall time to UTC; false when the local time does not exist.</summary>
    public static bool TryToUtc(TimeZoneInfo zone, DateTime local, out DateTimeOffset utc)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            utc = default;
            return false;
        }

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        utc = new DateTimeOffset(local, offset).ToUniversalTime();
        return true;
    }

    public static bool TryParseDateTime(string text, out DateTime local)
    {
        local = default;
        var match = DayFirst().Match(text);
        if (match.Success)
        {
            return TryCreate(match, "y", "m", "d", "h", "n", out local);
        }

        match = YearFirst().Match(text);
        return match.Success && TryCreate(match, "y", "m", "d", "h", "n", out local);
    }

    public static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        var match = Clock().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        if (hour > 23 || minute > 59)
        {
            return false;
        }

        time = new TimeSpan(hour, minute, 0);
        return true;
    }

    private static bool TryCreate(Match match, string year, string month, string day, string hour, string minute, out DateTime local)
    {
        local = default;
        var y = int.Parse(match.Groups[year].Value, CultureInfo.InvariantCulture);
        var m = int.Parse(match.Groups[month].Value, CultureInfo.InvariantCulture);
        var d = int.Parse(match.Groups[day].Value, CultureInfo.InvariantCulture);
        var h = int.Parse(match.Groups[hour].Value, CultureInfo.InvariantCulture);
        var n = int.Parse(match.Groups[minute].Value, CultureInfo.InvariantCulture);
        if (y < 1 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m) || h > 23 || n > 59)
        {
            return false;
        }

        local = new DateTime(y, m, d, h, n, 0, DateTimeKind.Unspecified);
        return true;
    }

    private static DateTimeOffset[] Scan(string id)
    {
        var zone = Find(id);
        var transitions = new List<DateTimeOffset>();
        var at = RangeStart;
        var previous = zone.GetUtcOffset(at);
        while (at < RangeEnd)
        {
            var next = at.AddHours(1);
            var offset = zone.GetUtcOffset(next);
            if (offset != previous)
            {
                var low = at;
                var high = next;
                while (high - low > TimeSpan.FromMinutes(1))
                {
                    var middle = low + ((high - low) / 2);
                    if (zone.GetUtcOffset(middle) == previous)
                    {
                        low = middle;
                    }
                    else
                    {
                        high = middle;
                    }
                }

                transitions.Add(high);
                previous = offset;
            }

            at = next;
        }

        return transitions.ToArray();
    }

    [GeneratedRegex("^(?<d>[0-9]{2})/(?<m>[0-9]{2})/(?<y>[0-9]{4}) (?<h>[0-9]{2}):(?<n>[0-9]{2})$")]
    private static partial Regex DayFirst();

    [GeneratedRegex("^(?<y>[0-9]{4})-(?<m>[0-9]{2})-(?<d>[0-9]{2}) (?<h>[0-9]{2}):(?<n>[0-9]{2})$")]
    private static partial Regex YearFirst();

    [GeneratedRegex("^(?<h>[0-9]{1,2}):(?<n>[0-9]{2})$")]
    private static partial Regex Clock();
}
