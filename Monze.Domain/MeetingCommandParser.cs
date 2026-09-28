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
            request = new MeetingRequest(MeetingScheduleKind.Now, null);
            return true;
        }

        var kind = head.Equals("once", StringComparison.OrdinalIgnoreCase)
            ? MeetingScheduleKind.Once
            : head.Equals("daily", StringComparison.OrdinalIgnoreCase)
                ? MeetingScheduleKind.Daily
                : head.Equals("weekly", StringComparison.OrdinalIgnoreCase)
                    ? MeetingScheduleKind.Weekly
                    : (MeetingScheduleKind?)null;
        if (kind is null || args.Count < 2)
        {
            return false;
        }

        request = new MeetingRequest(kind.Value, JoinArguments(args, 1));
        return true;
    }

    private static string JoinArguments(IReadOnlyList<string> args, int start)
    {
        var length = 0;
        for (var i = start; i < args.Count; i++)
        {
            length += args[i].Length;
            if (i > start)
            {
                length++;
            }
        }

        return string.Create(
            length,
            (Args: args, Start: start),
            static (destination, state) =>
            {
                var position = 0;
                for (var i = state.Start; i < state.Args.Count; i++)
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
