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
}
