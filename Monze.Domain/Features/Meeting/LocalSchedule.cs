using System.Globalization;

namespace Monze.Domain;

public static class LocalSchedule
{
    public static bool TryToUtc(
        DateOnly date,
        TimeOnly time,
        string timeZoneId,
        out DateTimeOffset utc,
        out string? note)
    {
        utc = default;
        note = null;
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            note = $"Unknown timezone {timeZoneId}.";
            return false;
        }

        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            note = "That local time does not exist.";
            return false;
        }

        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            var earlier = offsets.Max();
            note = $"Ambiguous local time. Using the earlier offset {earlier}.";
            utc = new DateTimeOffset(local, earlier).ToUniversalTime();
            return true;
        }

        utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return true;
    }

    /// <summary>
    /// The instant as dd/MM/yyyy HH:mm in the zone, formatted with the
    /// invariant culture: the host's culture would change the separator (tr-TR)
    /// or the calendar (ar-SA Hijri) (CAND-10).
    /// </summary>
    public static string Describe(DateTimeOffset instant, string timeZoneId)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return string.Create(CultureInfo.InvariantCulture, $"{TimeZoneInfo.ConvertTime(instant, zone):dd/MM/yyyy HH:mm} ({timeZoneId})");
        }
        catch (TimeZoneNotFoundException)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{instant.ToUniversalTime():dd/MM/yyyy HH:mm} UTC");
        }
        catch (InvalidTimeZoneException)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{instant.ToUniversalTime():dd/MM/yyyy HH:mm} UTC");
        }
    }
}
