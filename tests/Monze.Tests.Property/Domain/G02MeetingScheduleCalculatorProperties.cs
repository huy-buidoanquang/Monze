using System.Globalization;
using CsCheck;
using Monze.Domain;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Domain;

/// <summary>
/// G02: MeetingScheduleCalculator.TryGetNext against a brute-force oracle that
/// scans every candidate day (daily: yesterday..+8 days, weekly: +14 days) and
/// takes the earliest existing local time after now. "now" and the local time
/// are drawn around real offset transitions of 10 time zones.
/// Known gap CAND-09: daily inspects only today and tomorrow, weekly only the
/// next target day and the week after; when no existing local time after now
/// falls in that window (DST gap, skipped or repeated calendar day) they give
/// up instead of moving on. Production schedules use Asia/Ho_Chi_Minh, which
/// has no transitions.
/// ENV-01: Windows time zone data for Pacific/Apia around 2011-12-30 is not
/// self-consistent (GetUtcOffset and ConvertTime disagree); the oracle uses the
/// same TimeZoneInfo, so those inputs land in the CAND-09 window class.
/// </summary>
public sealed class G02MeetingScheduleCalculatorProperties
{
    private static readonly string[] Kinds = ["once", "daily", "weekly"];
    private static readonly string[] NowModes = ["transition", "random"];
    private static readonly string[] TimeModes = ["transition-local", "random", "malformed"];
    private static readonly string[] DayTokens = ["0", "1", "2", "3", "4", "5", "6", "cn", "t2", "t3", "t4", "t5", "t6", "t7", "sun", "monday", "fri", "saturday"];

    private static readonly Gen<ScheduleCase> Cases =
        from zone in Gen.OneOfConst(TimeZoneEdges.Zones)
        from kind in Gen.OneOfConst(Kinds)
        from nowMode in Gen.OneOfConst(NowModes)
        from timeMode in Gen.OneOfConst(TimeModes)
        from transitionIndex in Gen.Int[0, 1_000_000]
        from nowShiftMinutes in Gen.Int[-9 * 24 * 60, 9 * 24 * 60]
        from randomNowMinutes in Gen.Int[0, (int)((TimeZoneEdges.RangeEnd - TimeZoneEdges.RangeStart).TotalMinutes) - 1]
        from localShiftMinutes in Gen.Int[-45, 120]
        from dayShift in Gen.Int[-3, 10]
        from minuteOfDay in Gen.Int[0, 24 * 60 - 1]
        from format in Gen.Int[0, 17]
        select Build(zone, kind, nowMode, timeMode, transitionIndex, nowShiftMinutes, randomNowMinutes, localShiftMinutes, dayShift, minuteOfDay, format);

    [Fact]
    [Req("REQ-MTG-002")]
    [Covers("port:ISchedulingRepository.CreateMeetingScheduleAsync")]
    public void Next_run_is_the_earliest_existing_local_time_after_now()
    {
        PropertyRun.Run(
            "G02",
            Cases,
            Check,
            iterations: 300_000,
            declare: static ledger =>
            {
                ledger.Dimension("kind", Kinds).Dimension("zone", TimeZoneEdges.Zones).Dimension("now", NowModes).Dimension("time", TimeModes);
                foreach (var zone in TimeZoneEdges.Zones.Where(static zone => !TimeZoneEdges.HasTransitions(zone)))
                {
                    ledger.Infeasible("zone", zone, "now", "transition").Infeasible("zone", zone, "time", "transition-local");
                }
            },
            knownDefects: ["CAND-09"],
            print: static item => item.Describe());
    }

    private static PropertyResult Check(ScheduleCase item)
    {
        var tags = new Dictionary<string, string>
        {
            ["kind"] = item.Kind,
            ["zone"] = item.Zone,
            ["now"] = item.NowMode,
            ["time"] = item.TimeMode,
            ["format"] = item.Format
        };
        var zone = TimeZoneEdges.Find(item.Zone);
        var kind = item.Kind switch
        {
            "once" => MeetingScheduleKind.Once,
            "daily" => MeetingScheduleKind.Daily,
            _ => MeetingScheduleKind.Weekly
        };
        var ok = MeetingScheduleCalculator.TryGetNext(kind, item.WhenText, item.Zone, item.Now, out var next, out _);
        var expected = Oracle(item, zone, out var gapInImplementationWindow);
        var same = ok == expected.HasValue && (!ok || next == expected!.Value);
        var note = $"expected {(expected is { } value ? value.ToString("u", CultureInfo.InvariantCulture) : "reject")}, got {(ok ? next.ToString("u", CultureInfo.InvariantCulture) : "reject")}";
        if (gapInImplementationWindow)
        {
            return same
                ? PropertyResult.Pass(item.Describe(), tags, "CAND-09")
                : !ok ? PropertyResult.Known("CAND-09", item.Describe(), tags, note) : PropertyResult.Fail(item.Describe(), tags, note);
        }

        return PropertyResult.Check(same, item.Describe(), tags, () => note);
    }

    /// <summary>
    /// The earliest valid instant after now, or null. Also reports whether the
    /// days the implementation inspects (daily: today and tomorrow, weekly: the
    /// next target day and the week after) hold no existing local time after
    /// now, or hold a local time that does not exist.
    /// </summary>
    private static DateTimeOffset? Oracle(ScheduleCase item, TimeZoneInfo zone, out bool gapInImplementationWindow)
    {
        gapInImplementationWindow = false;
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.Now, zone).DateTime);
        switch (item.Kind)
        {
            case "once":
                return TimeZoneEdges.TryParseDateTime(item.WhenText, out var once)
                    && TimeZoneEdges.TryToUtc(zone, once, out var onceUtc)
                    && onceUtc > item.Now
                        ? onceUtc
                        : null;
            case "daily":
            {
                TimeSpan time;
                if (TimeZoneEdges.TryParseTime(item.WhenText, out var clock))
                {
                    time = clock;
                }
                else if (TimeZoneEdges.TryParseDateTime(item.WhenText, out var dated))
                {
                    time = dated.TimeOfDay;
                }
                else
                {
                    return null;
                }

                var window = new[] { At(localToday, time), At(localToday.AddDays(1), time) };
                gapInImplementationWindow = window.Any(zone.IsInvalidTime) || Earliest(zone, item.Now, window) is null;

                return Earliest(zone, item.Now, Enumerable.Range(-1, 10).Select(day => At(localToday.AddDays(day), time)));
            }

            default:
            {
                DayOfWeek target;
                TimeSpan time;
                if (TimeZoneEdges.TryParseDateTime(item.WhenText, out var dated))
                {
                    target = dated.DayOfWeek;
                    time = dated.TimeOfDay;
                }
                else
                {
                    var parts = item.WhenText.Split(' ');
                    if (parts.Length != 2 || !TryDay(parts[0], out target) || !TimeZoneEdges.TryParseTime(parts[1], out time))
                    {
                        return null;
                    }
                }

                var first = localToday.AddDays(((int)target - (int)localToday.DayOfWeek + 7) % 7);
                var weeks = new[] { At(first, time), At(first.AddDays(7), time) };
                gapInImplementationWindow = weeks.Any(zone.IsInvalidTime) || Earliest(zone, item.Now, weeks) is null;
                return Earliest(zone, item.Now, Enumerable.Range(0, 15)
                    .Select(day => localToday.AddDays(day))
                    .Where(date => date.DayOfWeek == target)
                    .Select(date => At(date, time)));
            }
        }
    }

    private static DateTimeOffset? Earliest(TimeZoneInfo zone, DateTimeOffset now, IEnumerable<DateTime> candidates)
    {
        DateTimeOffset? best = null;
        foreach (var local in candidates)
        {
            if (TimeZoneEdges.TryToUtc(zone, local, out var utc) && utc > now && (best is null || utc < best))
            {
                best = utc;
            }
        }

        return best;
    }

    private static DateTime At(DateOnly date, TimeSpan time)
        => date.ToDateTime(TimeOnly.FromTimeSpan(time), DateTimeKind.Unspecified);

    private static bool TryDay(string token, out DayOfWeek day)
    {
        day = token switch
        {
            "0" or "cn" or "sun" or "sunday" => DayOfWeek.Sunday,
            "1" or "t2" or "mon" or "monday" => DayOfWeek.Monday,
            "2" or "t3" or "tue" or "tuesday" => DayOfWeek.Tuesday,
            "3" or "t4" or "wed" or "wednesday" => DayOfWeek.Wednesday,
            "4" or "t5" or "thu" or "thursday" => DayOfWeek.Thursday,
            "5" or "t6" or "fri" or "friday" => DayOfWeek.Friday,
            "6" or "t7" or "sat" or "saturday" => DayOfWeek.Saturday,
            _ => (DayOfWeek)(-1)
        };
        return day >= DayOfWeek.Sunday;
    }

    private static ScheduleCase Build(
        string zoneId,
        string kind,
        string nowMode,
        string timeMode,
        int transitionIndex,
        int nowShiftMinutes,
        int randomNowMinutes,
        int localShiftMinutes,
        int dayShift,
        int minuteOfDay,
        int format)
    {
        var zone = TimeZoneEdges.Find(zoneId);
        var transitions = TimeZoneEdges.TransitionsOf(zoneId);
        var hasTransitions = transitions.Count > 0;
        if (!hasTransitions)
        {
            nowMode = nowMode == "transition" ? "random" : nowMode;
            timeMode = timeMode == "transition-local" ? "random" : timeMode;
        }

        var transition = hasTransitions ? transitions[transitionIndex % transitions.Count] : TimeZoneEdges.RangeStart;
        var now = nowMode == "transition"
            ? transition.AddMinutes(nowShiftMinutes)
            : TimeZoneEdges.RangeStart.AddMinutes(randomNowMinutes);

        // The wall time just before the transition is where gaps and overlaps start.
        var anchor = timeMode == "transition-local"
            ? TimeZoneInfo.ConvertTime(transition.AddMinutes(-1), zone).DateTime.AddMinutes(1 + localShiftMinutes)
            : TimeZoneInfo.ConvertTime(now, zone).DateTime.Date.AddMinutes(minuteOfDay);
        var local = anchor.AddDays(dayShift);
        string whenText;
        string formatName;
        if (timeMode == "malformed")
        {
            (formatName, whenText) = (format % 6) switch
            {
                0 => ("malformed", "25:00"),
                1 => ("malformed", "31/02/2026 10:00"),
                2 => ("malformed", "9 18:00"),
                3 => ("malformed", "abc"),
                4 => ("malformed", "10:00 extra"),
                _ => ("malformed", "2026-13-01 10:00")
            };
        }
        else
        {
            (formatName, whenText) = kind switch
            {
                "once" => (format % 2) == 0
                    ? ("dd/MM/yyyy HH:mm", local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture))
                    : ("yyyy-MM-dd HH:mm", local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                "daily" => (format % 3) switch
                {
                    0 => ("HH:mm", local.ToString("HH:mm", CultureInfo.InvariantCulture)),
                    1 => ("H:mm", local.ToString("H:mm", CultureInfo.InvariantCulture)),
                    _ => ("dd/MM/yyyy HH:mm", local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture))
                },
                _ => (format % 2) == 0
                    ? ("dd/MM/yyyy HH:mm", local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture))
                    : ("day HH:mm", $"{DayTokens[format % DayTokens.Length]} {local.ToString("HH:mm", CultureInfo.InvariantCulture)}")
            };
        }

        return new ScheduleCase(zoneId, kind, nowMode, timeMode, formatName, whenText, now);
    }

    public sealed record ScheduleCase(string Zone, string Kind, string NowMode, string TimeMode, string Format, string WhenText, DateTimeOffset Now)
    {
        public string Describe()
            => $"{Kind} '{WhenText}' in {Zone} at {Now.ToString("u", CultureInfo.InvariantCulture)}";
    }
}
