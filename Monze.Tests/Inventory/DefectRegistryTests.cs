using System.Text.Json;
using System.Text.RegularExpressions;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Inventory;

/// <summary>
/// The defect registry (tests/traceability/expected-gaps.json) and the
/// known-defect markers in tests and campaign scenarios must agree: every id
/// a test or scenario expects to fail is registered, ids are unique, and
/// every entry carries a severity and a location.
/// </summary>
public sealed partial class DefectRegistryTests
{
    private static readonly string[] SourceRoots = ["Monze.Tests", "tests"];

    [Fact]
    [Req("REQ-TRACE-002")]
    public void Every_known_defect_marker_is_registered()
    {
        var registered = Registry().Select(static entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var unregistered = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in SourceRoots)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Root, root), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in Marker().Matches(File.ReadAllText(file)))
                {
                    var id = match.Groups["id"].Value;
                    if (!registered.Contains(id))
                    {
                        unregistered.Add($"{id} ({Path.GetRelativePath(RepositoryPaths.Root, file)})");
                    }
                }
            }
        }

        Assert.True(unregistered.Count == 0, $"Known-defect ids missing from tests/traceability/expected-gaps.json:{Environment.NewLine}{string.Join(Environment.NewLine, unregistered)}");
    }

    [Fact]
    [Req("REQ-TRACE-002")]
    public void Registry_entries_are_unique_and_complete()
    {
        var entries = Registry();
        var duplicates = entries.GroupBy(static entry => entry.Id, StringComparer.Ordinal).Where(static group => group.Count() > 1).Select(static group => group.Key).ToList();
        var incomplete = entries
            .Where(static entry => entry.Severity is not ("P0" or "P1" or "P2" or "P3")
                || entry.Status is not ("open" or "not-reproduced" or "fixed" or "observation")
                || string.IsNullOrWhiteSpace(entry.Location)
                || string.IsNullOrWhiteSpace(entry.Title))
            .Select(static entry => entry.Id)
            .ToList();

        Assert.Empty(duplicates);
        Assert.Empty(incomplete);
    }

    private static List<(string Id, string Severity, string Status, string Location, string Title)> Registry()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "tests", "traceability", "expected-gaps.json")));
        return document.RootElement.GetProperty("defects").EnumerateArray()
            .Select(static entry => (
                entry.GetProperty("id").GetString()!,
                entry.GetProperty("severity").GetString()!,
                entry.GetProperty("status").GetString()!,
                entry.GetProperty("location").GetString()!,
                entry.GetProperty("title").GetString()!))
            .ToList();
    }

    // ExpectFailure("ID", ExpectFailureAsync("ID", KnownGap("ID", KnownDefect = "ID", PropertyResult.Known("ID".
    [GeneratedRegex(@"(?:ExpectFailure(?:Async)?\(|KnownGap\(|KnownDefect = |Known\()""(?<id>(?:DEF|CAND|SEC)-\d+)""")]
    private static partial Regex Marker();
}
