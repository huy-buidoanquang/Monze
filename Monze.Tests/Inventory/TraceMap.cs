using System.Text.Json.Nodes;

namespace Monze.Tests.Inventory;

/// <summary>
/// Builds the traceability artifact (monze.artifact.v1, kind traceability)
/// that section 7 of the campaign report renders. An item counts as mapped
/// when a test names it with [Covers] or a requirement declaring it has at
/// least one test; "observed" is filled by later tiers that record runtime
/// observations.
/// </summary>
internal static class TraceMap
{
    private const int UnmappedListLimit = 40;

    public static JsonObject Build(TraceabilityModel model)
    {
        var testsByRequirement = model.Requirements.ToDictionary(
            static requirement => requirement.Id,
            model.TestsFor,
            StringComparer.Ordinal);
        var covered = model.TestMethods.SelectMany(static test => test.Covers).ToHashSet(StringComparer.Ordinal);
        var rows = model.Items.Select(item =>
        {
            var requirements = model.RequirementsFor(item);
            var mapped = covered.Contains(item.Id) || requirements.Any(requirement => testsByRequirement[requirement.Id].Count > 0);
            return (Item: item, Declared: requirements.Count > 0, Excluded: requirements.Count == 0 && model.IsExcluded(item), Mapped: mapped);
        }).ToList();

        var groups = new JsonArray();
        foreach (var kind in rows.GroupBy(static row => row.Item.Kind).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            groups.Add(new JsonObject
            {
                ["name"] = kind.Key,
                ["total"] = kind.Count(),
                ["mapped"] = kind.Count(static row => row.Mapped),
                ["observed"] = 0,
                ["excluded"] = kind.Count(static row => row.Excluded)
            });
        }

        var requirementRows = new JsonArray();
        foreach (var requirement in model.Requirements)
        {
            requirementRows.Add(new JsonObject
            {
                ["id"] = requirement.Id,
                ["area"] = requirement.Area,
                ["title"] = requirement.Title,
                ["tests"] = testsByRequirement[requirement.Id].Count,
                ["items"] = rows.Count(row => requirement.Covers.Any(pattern => TraceabilityModel.Matches(pattern, row.Item))),
                ["legacyTestIds"] = new JsonArray(requirement.LegacyTestIds.Select(static id => (JsonNode)id).ToArray())
            });
        }

        var legacy = new JsonArray();
        foreach (var group in model.Requirements
                     .SelectMany(static requirement => requirement.LegacyTestIds.Select(id => (Id: id, Requirement: requirement)))
                     .GroupBy(static pair => pair.Id)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            legacy.Add(new JsonObject
            {
                ["testId"] = group.Key,
                ["requirements"] = new JsonArray(group.Select(static pair => (JsonNode)pair.Requirement.Id).ToArray()),
                ["tests"] = group.SelectMany(pair => testsByRequirement[pair.Requirement.Id]).Select(static test => test.Name).Distinct().Count()
            });
        }

        var undeclared = rows.Count(static row => !row.Declared && !row.Excluded);
        var unknownRequirements = model.TestMethods
            .SelectMany(static test => test.Requirements)
            .Count(id => !testsByRequirement.ContainsKey(id));
        var mappedCount = rows.Count(static row => row.Mapped);
        var unmapped = rows.Where(static row => row.Declared && !row.Mapped).Select(static row => row.Item.Id).ToList();
        return new JsonObject
        {
            ["schema"] = "monze.artifact.v1",
            ["kind"] = "traceability",
            ["id"] = "trace-map",
            ["title"] = "Inventory và traceability",
            ["verdict"] = undeclared == 0 && unknownRequirements == 0 ? "PASS" : "FAIL",
            ["metrics"] = new JsonObject
            {
                ["inventoryItems"] = rows.Count,
                ["declared"] = rows.Count(static row => row.Declared),
                ["excluded"] = rows.Count(static row => row.Excluded),
                ["undeclared"] = undeclared,
                ["mappedItems"] = mappedCount,
                ["mappedPercent"] = rows.Count == 0 ? 0 : Math.Round(100.0 * mappedCount / rows.Count, 1),
                ["requirements"] = model.Requirements.Count,
                ["requirementsWithTests"] = testsByRequirement.Count(static pair => pair.Value.Count > 0),
                ["testMethods"] = model.TestMethods.Count,
                ["legacyTestIds"] = legacy.Count
            },
            ["invariants"] = new JsonArray
            {
                Invariant("TRACE-DRIFT", "0 mục inventory chưa khai báo", undeclared),
                Invariant("TRACE-REQ", "0 [Req] trỏ tới requirement chưa khai báo", unknownRequirements)
            },
            ["defectIds"] = new JsonArray(),
            ["notes"] = unmapped.Count == 0
                ? string.Empty
                : $"{unmapped.Count} mục đã khai báo nhưng requirement chưa có test: {string.Join(", ", unmapped.Take(UnmappedListLimit))}{(unmapped.Count > UnmappedListLimit ? ", …" : string.Empty)}",
            ["durationMinutes"] = 0,
            ["groups"] = groups,
            ["requirements"] = requirementRows,
            ["legacy"] = legacy
        };
    }

    private static JsonObject Invariant(string id, string expected, int observed)
        => new()
        {
            ["id"] = id,
            ["expected"] = expected,
            ["observed"] = observed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["pass"] = observed == 0
        };
}
