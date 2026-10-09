using System.Globalization;

namespace Monze.Domain;

public static class MeetingScheduleCalculator
{
    public static bool TryGetNext(
        MeetingScheduleKind kind,
        string whenText,
        string timeZoneId,
        DateTimeOffset now,
        out DateTimeOffset next,
        out string? error)
    {
        next = default;
        error = null;
        if (string.IsNullOrWhiteSpace(whenText))
        {
            error = "Thiếu thời điểm.";
            return false;
        }

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            error = $"Không tìm thấy timezone {timeZoneId}.";
            return false;
        }

        return kind switch
        {
            MeetingScheduleKind.Once => TryOnce(whenText, zone, now, out next, out error),
            MeetingScheduleKind.Daily => TryDaily(whenText, zone, now, out next, out error),
            MeetingScheduleKind.Weekly => TryWeekly(whenText, zone, now, out next, out error),
            _ => false
        };
    }

    private static bool TryOnce(
        string text,
        TimeZoneInfo zone,
        DateTimeOffset now,
        out DateTimeOffset next,
        out string? error)
    {
        next = default;
        error = null;
        if (!TryParseLocalDateTime(text, out var local))
        {
            error = "Dùng thời điểm dạng dd/MM/yyyy HH:mm.";
            return false;
        }

        if (!LocalSchedule.TryToUtc(
                DateOnly.FromDateTime(local),
                TimeOnly.FromDateTime(local),
                zone.Id,
                out next,
                out error))
        {
            return false;
        }

        if (next <= now)
        {
            error = "Thời điểm phải nằm trong tương lai.";
            return false;
        }

        return true;
    }

    private static bool TryDaily(
        string text,
        TimeZoneInfo zone,
        DateTimeOffset now,
        out DateTimeOffset next,
        out string? error)
    {
        next = default;
        error = null;
        TimeOnly time;
        if (TryParseTime(text, out var parsedTime))
        {
            time = parsedTime;
        }
        else if (TryParseLocalDateTime(text, out var localDateTime))
        {
            time = TimeOnly.FromDateTime(localDateTime);
        }
        else
        {
            error = "Dùng giờ dạng 18:00.";
            return false;
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return TryEarliest(today.AddDays(-1), 10, null, time, zone, now, out next, out error);
    }

    private static bool TryWeekly(
        string text,
        TimeZoneInfo zone,
        DateTimeOffset now,
        out DateTimeOffset next,
        out string? error)
    {
        next = default;
        error = null;
        DayOfWeek targetDay;
        TimeOnly time;
        if (TryParseLocalDateTime(text, out var localDateTime))
        {
            targetDay = localDateTime.DayOfWeek;
            time = TimeOnly.FromDateTime(localDateTime);
        }
        else
        {
            var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !TryDay(parts[0], out targetDay)
                || !TryParseTime(parts[1], out time))
            {
                error = "Dùng dd/MM/yyyy HH:mm hoặc thứ và giờ, ví dụ weekly 1 18:00.";
                return false;
            }
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return TryEarliest(today, 15, targetDay, time, zone, now, out next, out error);
    }

    /// <summary>
    /// The earliest instant after <paramref name="now"/> at <paramref name="time"/>
    /// on the local dates [from, from + days) (only <paramref name="day"/>s when
    /// given). A date whose local time does not exist (a DST gap, a skipped
    /// calendar day) is passed over instead of ending the search (CAND-09).
    /// </summary>
    private static bool TryEarliest(
        DateOnly from,
        int days,
        DayOfWeek? day,
        TimeOnly time,
        TimeZoneInfo zone,
        DateTimeOffset now,
        out DateTimeOffset next,
        out string? error)
    {
        next = default;
        error = null;
        var found = false;
        for (var i = 0; i < days; i++)
        {
            var date = from.AddDays(i);
            if (day is { } wanted && date.DayOfWeek != wanted)
            {
                continue;
            }

            if (!LocalSchedule.TryToUtc(date, time, zone.Id, out var candidate, out var candidateError))
            {
                error ??= candidateError;
                continue;
            }

            if (candidate > now && (!found || candidate < next))
            {
                next = candidate;
                found = true;
            }
        }

        if (found)
        {
            error = null;
        }

        return found;
    }

    private static bool TryDay(string text, out DayOfWeek day)
    {
        if (int.TryParse(text, out var number) && number is >= 0 and <= 6)
        {
            day = (DayOfWeek)number;
            return true;
        }

        day = text.ToLowerInvariant() switch
        {
            "sun" or "sunday" or "cn" => DayOfWeek.Sunday,
            "mon" or "monday" or "t2" => DayOfWeek.Monday,
            "tue" or "tuesday" or "t3" => DayOfWeek.Tuesday,
            "wed" or "wednesday" or "t4" => DayOfWeek.Wednesday,
            "thu" or "thursday" or "t5" => DayOfWeek.Thursday,
            "fri" or "friday" or "t6" => DayOfWeek.Friday,
            "sat" or "saturday" or "t7" => DayOfWeek.Saturday,
            _ => (DayOfWeek)(-1)
        };
        return day >= DayOfWeek.Sunday && day <= DayOfWeek.Saturday;
    }

    private static bool TryParseLocalDateTime(string text, out DateTime local)
    {
        var formats = new[] { "dd/MM/yyyy HH:mm", "dd/MM/yyyy H:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm" };
        return DateTime.TryParseExact(
            text,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out local);
    }

    private static bool TryParseTime(string text, out TimeOnly time)
        => TimeOnly.TryParseExact(
            text,
            ["HH:mm", "H:mm"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out time);
}
