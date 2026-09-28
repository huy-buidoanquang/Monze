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
        if (!DateTime.TryParse(
                text,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var local))
        {
            error = "Dùng thời điểm dạng YYYY-MM-DD HH:mm.";
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
        if (!TimeOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var time))
        {
            error = "Dùng giờ dạng 18:00.";
            return false;
        }

        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var date = DateOnly.FromDateTime(localNow.DateTime);
        for (var i = 0; i < 2; i++)
        {
            if (LocalSchedule.TryToUtc(date, time, zone.Id, out next, out error) && next > now)
            {
                return true;
            }

            date = date.AddDays(1);
        }

        return false;
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
        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !TryDay(parts[0], out var targetDay)
            || !TimeOnly.TryParse(parts[1], CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var time))
        {
            error = "Dùng thứ và giờ, ví dụ weekly 1 18:00.";
            return false;
        }

        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var date = DateOnly.FromDateTime(localNow.DateTime);
        var days = ((int)targetDay - (int)localNow.DayOfWeek + 7) % 7;
        date = date.AddDays(days);
        if (!LocalSchedule.TryToUtc(date, time, zone.Id, out next, out error))
        {
            return false;
        }

        if (next <= now)
        {
            date = date.AddDays(7);
            return LocalSchedule.TryToUtc(date, time, zone.Id, out next, out error);
        }

        return true;
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
}
