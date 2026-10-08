using System.Text.Json;
using System.Text.RegularExpressions;
using Monze.Testing;

namespace Monze.Tests.Inventory;

internal sealed record Requirement(
    string Id,
    string Area,
    string Title,
    IReadOnlyList<string> LegacyTestIds,
    IReadOnlyList<string> Covers,
    IReadOnlyList<string> Tests);

internal sealed record Exclusion(string Pattern, string Reason);

internal sealed record TestMethod(string Name, string File, IReadOnlyList<string> Requirements, IReadOnlyList<string> Covers);

/// <summary>
/// Joins the extracted inventory with tests/traceability/requirements.json,
/// exclusions.json and the test methods of every test project. Test methods
/// are found in source, so projects that this assembly does not reference are
/// included as well.
/// </summary>
internal sealed partial class TraceabilityModel
{
    private static readonly string[] TestRoots = ["Monze.Tests", "tests"];
    private static readonly string[] NonTestProjects = ["Monze.Testing", "Monze.TestReport", "Build", "traceability"];

    private TraceabilityModel(
        IReadOnlyList<InventoryItem> items,
        IReadOnlyList<Requirement> requirements,
        IReadOnlyList<Exclusion> exclusions,
        IReadOnlyList<TestMethod> tests)
    {
        Items = items;
        Requirements = requirements;
        Exclusions = exclusions;
        TestMethods = tests;
    }

    public IReadOnlyList<InventoryItem> Items { get; }

    public IReadOnlyList<Requirement> Requirements { get; }

    public IReadOnlyList<Exclusion> Exclusions { get; }

    public IReadOnlyList<TestMethod> TestMethods { get; }

    public static TraceabilityModel Load()
    {
        var directory = Path.Combine(RepositoryPaths.Root, "tests", "traceability");
        using var requirements = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "requirements.json")));
        using var exclusions = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "exclusions.json")));
        return new TraceabilityModel(
            InventoryCatalog.Extract(),
            requirements.RootElement.GetProperty("requirements").EnumerateArray().Select(static element => new Requirement(
                element.GetProperty("id").GetString()!,
                element.GetProperty("area").GetString()!,
                element.GetProperty("title").GetString()!,
                Strings(element, "legacyTestIds"),
                Strings(element, "covers"),
                Strings(element, "tests"))).ToList(),
            exclusions.RootElement.GetProperty("exclusions").EnumerateArray().Select(static element => new Exclusion(
                element.GetProperty("pattern").GetString()!,
                element.GetProperty("reason").GetString()!)).ToList(),
            FindTestMethods());
    }

    public IReadOnlyList<Requirement> RequirementsFor(InventoryItem item)
        => Requirements.Where(requirement => requirement.Covers.Any(pattern => Matches(pattern, item))).ToList();

    public bool IsExcluded(InventoryItem item)
        => Exclusions.Any(exclusion => Matches(exclusion.Pattern, item));

    public IReadOnlyList<TestMethod> TestsFor(Requirement requirement)
        => TestMethods.Where(test =>
                test.Requirements.Contains(requirement.Id, StringComparer.Ordinal)
                || requirement.Tests.Any(glob => Glob(glob).IsMatch(test.Name)))
            .ToList();

    /// <summary>
    /// "kind:glob" matches the item id; "kind@glob" matches items of that kind
    /// whose source file matches the glob.
    /// </summary>
    public static bool Matches(string pattern, InventoryItem item)
    {
        var at = pattern.IndexOf('@', StringComparison.Ordinal);
        var colon = pattern.IndexOf(':', StringComparison.Ordinal);
        if (at > 0 && (colon < 0 || at < colon))
        {
            return string.Equals(pattern[..at], item.Kind, StringComparison.Ordinal)
                && Glob(pattern[(at + 1)..]).IsMatch(item.Source);
        }

        return Glob(pattern).IsMatch(item.Id);
    }

    public static Regex Glob(string glob)
        => new("^" + Regex.Escape(glob).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant);

    private static IReadOnlyList<string> Strings(JsonElement element, string name)
        => element.TryGetProperty(name, out var values)
            ? values.EnumerateArray().Select(static value => value.GetString()!).ToList()
            : [];

    private static IReadOnlyList<TestMethod> FindTestMethods()
    {
        var root = RepositoryPaths.Root;
        var tests = new List<TestMethod>();
        foreach (var testRoot in TestRoots)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, testRoot), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/obj/", StringComparison.Ordinal)
                    || NonTestProjects.Any(project => relative.StartsWith($"tests/{project}/", StringComparison.Ordinal)))
                {
                    continue;
                }

                ReadTestMethods(relative, File.ReadAllLines(file), tests);
            }
        }

        return tests;
    }

    private static void ReadTestMethods(string file, string[] lines, List<TestMethod> tests)
    {
        string? testClass = null;
        var isTest = false;
        var requirements = new List<string>();
        var covers = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (TestClass().Match(line) is { Success: true } classMatch)
            {
                testClass = classMatch.Groups["name"].Value;
                continue;
            }

            if (line.StartsWith('['))
            {
                isTest |= TestAttribute().IsMatch(line);
                foreach (Match attribute in ReqAttribute().Matches(line))
                {
                    requirements.AddRange(QuotedValue().Matches(attribute.Groups["args"].Value).Select(static value => value.Groups["value"].Value));
                }

                foreach (Match attribute in CoversAttribute().Matches(line))
                {
                    covers.AddRange(QuotedValue().Matches(attribute.Groups["args"].Value).Select(static value => value.Groups["value"].Value));
                }

                continue;
            }

            if (isTest && testClass is not null && TestMethodSignature().Match(line) is { Success: true } method)
            {
                tests.Add(new TestMethod($"{testClass}.{method.Groups["name"].Value}", file, requirements.ToList(), covers.ToList()));
            }

            if (line.Length > 0)
            {
                isTest = false;
                requirements.Clear();
                covers.Clear();
            }
        }
    }

    [GeneratedRegex("\\bclass (?<name>\\w+Tests)\\b")]
    private static partial Regex TestClass();

    [GeneratedRegex("^\\[(Fact|Theory|DbFact|DbTheory|RedisFact)\\b")]
    private static partial Regex TestAttribute();

    [GeneratedRegex("\\bReq\\((?<args>[^)]*)\\)")]
    private static partial Regex ReqAttribute();

    [GeneratedRegex("\\bCovers\\((?<args>[^)]*)\\)")]
    private static partial Regex CoversAttribute();

    [GeneratedRegex("\"(?<value>[^\"]+)\"")]
    private static partial Regex QuotedValue();

    [GeneratedRegex("^public\\s+(?:async\\s+)?(?:Task|void|ValueTask)\\s+(?<name>\\w+)\\s*\\(")]
    private static partial Regex TestMethodSignature();
}
