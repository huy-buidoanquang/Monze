using System.Globalization;
using CsCheck;
using Monze.Domain;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Domain;

/// <summary>
/// G03: LocalSchedule.TryToUtc and Describe around gaps and overlaps.
/// A local time that does not exist is rejected; an ambiguous one maps to the
/// earlier instant; every accepted instant converts back to the same wall
/// time; Describe prints dd/MM/yyyy HH:mm in the Gregorian calendar.
/// Regression for CAND-10: Describe used the host culture, so tr-TR printed
/// "08.10.2026" and ar-SA a Hijri date.
/// ENV-01: where the platform's own conversions disagree (Windows data for
/// Pacific/Apia around 2011-12-30), the round trip cannot hold for any code;
/// those inputs are recorded as "skip" with tag platform=inconsistent.
/// </summary>
public sealed class G03LocalScheduleProperties
{
    private const string UnknownZone = "Invalid/Zone";
    private static readonly string[] Zones = [.. TimeZoneEdges.Zones, UnknownZone];
    private static readonly string[] Modes = ["transition-local", "random"];

    private static readonly Gen<LocalCase> Cases =
        from zone in Gen.OneOfConst(Zones)
        from mode in Gen.OneOfConst(Modes)
        from culture in Cultures.Gen
        from transitionIndex in Gen.Int[0, 1_000_000]
        from shiftMinutes in Gen.Int[-90, 150]
        from randomMinutes in Gen.Int[0, (int)((TimeZoneEdges.RangeEnd - TimeZoneEdges.RangeStart).TotalMinutes) - 1]
        select Build(zone, mode, culture, transitionIndex, shiftMinutes, randomMinutes);

    [Fact]
    [Req("REQ-MTG-002")]
    public void Local_times_convert_to_one_instant_and_back()
    {
        PropertyRun.Run(
            "G03",
            Cases,
            static item =>
            {
                var tags = new Dictionary<string, string> { ["zone"] = item.Zone, ["mode"] = item.Mode, ["culture"] = item.Culture };
                var input = $"{item.Local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} {item.Zone} [{item.Culture}]";
                var date = DateOnly.FromDateTime(item.Local);
                var time = TimeOnly.FromDateTime(item.Local);
                var accepted = LocalSchedule.TryToUtc(date, time, item.Zone, out var utc, out _);
                if (item.Zone == UnknownZone)
                {
                    var unknownDescription = Cultures.Run(item.Culture, () => LocalSchedule.Describe(item.Local, item.Zone));
                    return PropertyResult.Check(!accepted && unknownDescription.EndsWith(" UTC", StringComparison.Ordinal), input, tags, () => "unknown zone must be rejected and described in UTC");
                }

                var zone = TimeZoneEdges.Find(item.Zone);
                var exists = TimeZoneEdges.TryToUtc(zone, item.Local, out var expected);
                if (accepted != exists || (accepted && utc != expected))
                {
                    return PropertyResult.Fail(input, tags, $"expected {(exists ? expected.ToString("u", CultureInfo.InvariantCulture) : "reject")}, got {(accepted ? utc.ToString("u", CultureInfo.InvariantCulture) : "reject")}");
                }

                if (!accepted)
                {
                    return PropertyResult.Pass(input, tags);
                }

                if (TimeZoneInfo.ConvertTime(expected, zone).DateTime != item.Local)
                {
                    tags["platform"] = "inconsistent";
                    return new PropertyResult("skip", input, tags, "ENV-01: platform time zone data does not round-trip this local time");
                }

                if (TimeZoneInfo.ConvertTime(utc, zone).DateTime != item.Local)
                {
                    return PropertyResult.Fail(input, tags, "accepted instant does not convert back to the same wall time");
                }

                var description = Cultures.Run(item.Culture, () => LocalSchedule.Describe(utc, item.Zone));
                var invariant = $"{item.Local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)} ({item.Zone})";
                return PropertyResult.Check(description == invariant, input, tags, () => $"Describe printed '{description}', expected '{invariant}'");
            },
            iterations: 80_000,
            declare: static ledger =>
            {
                ledger.Dimension("zone", Zones).Dimension("mode", Modes).Dimension("culture", Cultures.Names);
                foreach (var zone in Zones.Where(static zone => zone == UnknownZone || !TimeZoneEdges.HasTransitions(zone)))
                {
                    ledger.Infeasible("zone", zone, "mode", "transition-local");
                }
            },
            print: static item => $"{item.Local:O} {item.Zone} {item.Culture}");
    }

    private static LocalCase Build(string zoneId, string mode, string culture, int transitionIndex, int shiftMinutes, int randomMinutes)
    {
        var random = TimeZoneEdges.RangeStart.UtcDateTime.AddMinutes(randomMinutes);
        if (zoneId == UnknownZone || !TimeZoneEdges.HasTransitions(zoneId))
        {
            return new LocalCase(zoneId, "random", culture, random);
        }

        var zone = TimeZoneEdges.Find(zoneId);
        if (mode == "random")
        {
            return new LocalCase(zoneId, mode, culture, TimeZoneInfo.ConvertTime(new DateTimeOffset(random, TimeSpan.Zero), zone).DateTime);
        }

        var transitions = TimeZoneEdges.TransitionsOf(zoneId);
        var transition = transitions[transitionIndex % transitions.Count];
        var wall = TimeZoneInfo.ConvertTime(transition.AddMinutes(-1), zone).DateTime.AddMinutes(1 + shiftMinutes);
        return new LocalCase(zoneId, mode, culture, new DateTime(wall.Ticks - (wall.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Unspecified));
    }

    public sealed record LocalCase(string Zone, string Mode, string Culture, DateTime Local);
}
