namespace Monze.Domain;

public static class MeetingCommandParser
{
    public static bool TryParse(IReadOnlyList<string> args, out MeetingRequest? request)
    {
        request = null;
        if (args.Count == 0)
        {
            return false;
        }

        var head = args[0];
        if (head.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count == 1)
            {
                request = new MeetingRequest(MeetingScheduleKind.Now, null);
                return true;
            }

            return false;
        }

        if (head.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            // ASCII digits only: no sign, no spaces, no culture-specific digits (CAND-03).
            if (args.Count == 2
                && long.TryParse(args[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var scheduleId)
                && scheduleId > 0)
            {
                request = new MeetingRequest(MeetingScheduleKind.Once, null, null, scheduleId);
                return true;
            }

            return false;
        }

        if (TryParseNamedSchedule(args, out request))
        {
            return true;
        }

        var legacyKind = ParseKind(head);
        if (legacyKind is not null)
        {
            // Keep the original syntax working for existing users:
            // *meeting daily 09:00 and *meeting weekly 1 18:00.
            if (args.Count < 2)
            {
                return false;
            }

            request = new MeetingRequest(legacyKind.Value, null, JoinArguments(args, 1), null);
            return true;
        }

        return false;
    }

    private static bool TryParseNamedSchedule(
        IReadOnlyList<string> args,
        out MeetingRequest? request)
    {
        request = null;
        if (args.Count < 3)
        {
            return false;
        }

        var kind = MeetingScheduleKind.Once;
        var dateIndex = FindDate(args, 1);
        if (dateIndex < 1 || dateIndex + 1 >= args.Count || !IsTime(args[dateIndex + 1]))
        {
            return false;
        }

        // A frequency followed directly by a date/time remains legacy syntax.
        if (dateIndex == 1 && args.Count == 3 && ParseKind(args[0]) is not null)
        {
            return false;
        }

        var nameEnd = dateIndex;
        var kindBeforeDate = false;
        if (dateIndex > 1 && ParseKind(args[dateIndex - 1]) is { } explicitKind)
        {
            kind = explicitKind;
            kindBeforeDate = true;
            nameEnd--;
        }

        var name = JoinArguments(args, 0, nameEnd);
        if (name.Length == 0 || name.Length > 120)
        {
            return false;
        }

        var whenText = args[dateIndex] + " " + args[dateIndex + 1];
        var suffixIndex = dateIndex + 2;
        if (suffixIndex < args.Count)
        {
            // One frequency at most: before the date or after the time (CAND-08).
            if (ParseKind(args[suffixIndex]) is { } suffixKind
                && suffixIndex + 1 == args.Count
                && !kindBeforeDate)
            {
                kind = suffixKind;
            }
            else
            {
                return false;
            }
        }

        request = new MeetingRequest(kind, name, whenText, null);
        return true;
    }

    private static MeetingScheduleKind? ParseKind(string value)
        => value.Equals("once", StringComparison.OrdinalIgnoreCase)
            ? MeetingScheduleKind.Once
            : value.Equals("daily", StringComparison.OrdinalIgnoreCase)
                ? MeetingScheduleKind.Daily
                : value.Equals("weekly", StringComparison.OrdinalIgnoreCase)
                    ? MeetingScheduleKind.Weekly
                    : null;

    private static int FindDate(IReadOnlyList<string> args, int start)
    {
        for (var i = start; i < args.Count; i++)
        {
            if (IsDate(args[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsDate(string value)
        => DateTime.TryParseExact(
            value,
            "dd/MM/yyyy",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out _);

    // H:mm too, as the daily form accepts it (CAND-02).
    private static bool IsTime(string value)
        => TimeOnly.TryParseExact(
            value,
            ["HH:mm", "H:mm"],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out _);

    private static string JoinArguments(IReadOnlyList<string> args, int start)
        => JoinArguments(args, start, args.Count);

    private static string JoinArguments(IReadOnlyList<string> args, int start, int end)
    {
        var length = 0;
        for (var i = start; i < end; i++)
        {
            length += args[i].Length;
            if (i > start)
            {
                length++;
            }
        }

        return string.Create(
            length,
            (Args: args, Start: start, End: end),
            static (destination, state) =>
            {
                var position = 0;
                for (var i = state.Start; i < state.End; i++)
                {
                    if (i > state.Start)
                    {
                        destination[position++] = ' ';
                    }

                    var value = state.Args[i];
                    value.AsSpan().CopyTo(destination[position..]);
                    position += value.Length;
                }
            });
    }
}
