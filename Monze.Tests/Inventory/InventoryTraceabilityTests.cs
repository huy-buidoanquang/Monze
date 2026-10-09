using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Inventory;

public sealed partial class InventoryTraceabilityTests
{
    private static readonly Lazy<TraceabilityModel> Model = new(TraceabilityModel.Load);

    [Fact]
    [Req("REQ-TRACE-001")]
    public void Every_inventory_item_is_declared_by_a_requirement_or_an_exclusion()
    {
        var model = Model.Value;

        var undeclared = model.Items
            .Where(item => model.RequirementsFor(item).Count == 0 && !model.IsExcluded(item))
            .Select(static item => $"{item.Id} ({item.Source})")
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            $"{undeclared.Count} inventory items are not declared in tests/traceability:{Environment.NewLine}{string.Join(Environment.NewLine, undeclared)}");
    }

    [Fact]
    [Req("REQ-TRACE-002")]
    public void Requirement_and_exclusion_patterns_all_match_something()
    {
        var model = Model.Value;
        var stale = new List<string>();
        foreach (var requirement in model.Requirements)
        {
            stale.AddRange(requirement.Covers
                .Where(pattern => !model.Items.Any(item => TraceabilityModel.Matches(pattern, item)))
                .Select(pattern => $"{requirement.Id} covers {pattern}"));
            stale.AddRange(requirement.Tests
                .Where(glob => !model.TestMethods.Any(test => TraceabilityModel.Glob(glob).IsMatch(test.Name)))
                .Select(glob => $"{requirement.Id} tests {glob}"));
        }

        stale.AddRange(model.Exclusions
            .Where(exclusion => !model.Items.Any(item => TraceabilityModel.Matches(exclusion.Pattern, item)))
            .Select(static exclusion => $"exclusion {exclusion.Pattern}"));

        Assert.True(stale.Count == 0, $"Stale traceability patterns:{Environment.NewLine}{string.Join(Environment.NewLine, stale)}");
    }

    [Fact]
    [Req("REQ-TRACE-002")]
    public void Every_Req_attribute_names_a_declared_requirement()
    {
        var model = Model.Value;
        var declared = model.Requirements.Select(static requirement => requirement.Id).ToHashSet(StringComparer.Ordinal);

        var unknown = model.TestMethods
            .SelectMany(static test => test.Requirements.Select(id => (test.Name, Id: id)))
            .Where(reference => !declared.Contains(reference.Id))
            .Select(static reference => $"{reference.Name} -> {reference.Id}")
            .ToList();

        Assert.True(unknown.Count == 0, $"Tests reference undeclared requirements:{Environment.NewLine}{string.Join(Environment.NewLine, unknown)}");
    }

    [Fact]
    [Req("REQ-TRACE-002")]
    public void Every_Covers_attribute_names_an_inventory_item()
    {
        var model = Model.Value;
        var items = model.Items.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);

        var unknown = model.TestMethods
            .SelectMany(static test => test.Covers.Select(id => (test.Name, Id: id)))
            .Where(reference => !items.Contains(reference.Id))
            .Select(static reference => $"{reference.Name} -> {reference.Id}")
            .ToList();

        Assert.True(unknown.Count == 0, $"Tests cover unknown inventory items:{Environment.NewLine}{string.Join(Environment.NewLine, unknown)}");
    }

    [Fact]
    [Req("REQ-TRACE-002")]
    public void Requirement_ids_are_unique_and_well_formed()
    {
        var ids = Model.Value.Requirements.Select(static requirement => requirement.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, static id => Assert.Matches(RequirementId(), id));
        Assert.All(Model.Value.Exclusions, static exclusion => Assert.False(string.IsNullOrWhiteSpace(exclusion.Reason)));
    }

    [Fact]
    [Req("REQ-TRACE-001")]
    public void Trace_map_is_written_for_the_campaign()
    {
        var artifact = TraceMap.Build(Model.Value);

        Assert.Equal("PASS", artifact["verdict"]!.GetValue<string>());
        if (CampaignEnvironment.ArtifactDirectory is { } directory)
        {
            var path = Path.Combine(directory, "traceability", "trace-map.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, artifact.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        }
    }

    [GeneratedRegex("^REQ-[A-Z]+-\\d{3}$")]
    private static partial Regex RequirementId();
}
