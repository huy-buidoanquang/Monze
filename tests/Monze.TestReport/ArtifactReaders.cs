using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Monze.TestReport;

internal static partial class ArtifactReaders
{
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static CampaignManifest ReadManifest(string path)
    {
        if (!File.Exists(path))
        {
            return new CampaignManifest(
                "unknown",
                "unknown",
                DateTimeOffset.UtcNow,
                null,
                "unknown",
                "unknown",
                false,
                new Dictionary<string, string>(),
                [],
                ["raw/manifest.json was not written; the orchestrator did not finish."]);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var tiers = new List<TierRecord>();
        if (root.TryGetProperty("tiers", out var tierArray) && tierArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var tier in tierArray.EnumerateArray())
            {
                tiers.Add(new TierRecord(
                    String(tier, "name") ?? "unknown",
                    String(tier, "status") ?? TierStatus.NotRun,
                    Number(tier, "durationSeconds") ?? 0,
                    tier.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number ? exit.GetInt32() : null,
                    String(tier, "command"),
                    String(tier, "note")));
            }
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in env.EnumerateObject())
            {
                environment[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
            }
        }

        var notRun = new List<string>();
        if (root.TryGetProperty("notRun", out var notRunArray) && notRunArray.ValueKind == JsonValueKind.Array)
        {
            notRun.AddRange(notRunArray.EnumerateArray().Select(static item => item.GetString() ?? string.Empty));
        }

        return new CampaignManifest(
            String(root, "campaignId") ?? "unknown",
            String(root, "profile") ?? "unknown",
            Date(root, "startedUtc") ?? DateTimeOffset.UtcNow,
            Date(root, "finishedUtc"),
            String(root, "commit") ?? "unknown",
            String(root, "branch") ?? "unknown",
            root.TryGetProperty("dirty", out var dirty) && dirty.ValueKind == JsonValueKind.True,
            environment,
            tiers,
            notRun);
    }

    public static IReadOnlyList<TestProjectSummary> ReadTrx(string directory)
    {
        var summaries = new List<TestProjectSummary>();
        if (!Directory.Exists(directory))
        {
            return summaries;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.trx", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var project = Path.GetFileNameWithoutExtension(file);
            var document = XDocument.Load(file);
            var results = document.Descendants(Trx + "UnitTestResult")
                .Select(result => new TestResult(
                    project,
                    MethodName((string?)result.Attribute("testName") ?? "unknown"),
                    (string?)result.Attribute("outcome") ?? "Unknown",
                    TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var duration)
                        ? duration.TotalSeconds
                        : 0,
                    result.Descendants(Trx + "Message").FirstOrDefault()?.Value))
                .ToList();
            var failures = results.Where(static result => result.Outcome is "Failed" or "Error" or "Timeout" or "Aborted").ToList();
            var skipped = results.Where(static result => result.Outcome is "NotExecuted" or "Inconclusive").ToList();
            summaries.Add(new TestProjectSummary(
                project,
                results.Count,
                results.Count(static result => result.Outcome == "Passed"),
                failures.Count,
                skipped.Count,
                results.Sum(static result => result.DurationSeconds),
                failures,
                skipped));
        }

        return summaries;
    }

    public static CoverageSummary? ReadCoverage(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var files = Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories)
            .Where(static path => path.Contains("cobertura", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (files.Count == 0)
        {
            return null;
        }

        // Merge every Cobertura file by (assembly, file, line): max hits and
        // max covered conditions. Branch merging is an approximation when two
        // runs cover different conditions of the same line.
        var lines = new Dictionary<(string Assembly, string File, int Line), (long Hits, int Covered, int Total)>();
        foreach (var file in files)
        {
            var document = XDocument.Load(file);
            foreach (var package in document.Descendants("package"))
            {
                var assembly = (string?)package.Attribute("name") ?? "unknown";
                foreach (var @class in package.Descendants("class"))
                {
                    var fileName = NormalizeSourcePath((string?)@class.Attribute("filename") ?? "unknown");
                    foreach (var line in @class.Element("lines")?.Elements("line") ?? [])
                    {
                        var number = (int?)line.Attribute("number") ?? 0;
                        var hits = (long?)line.Attribute("hits") ?? 0;
                        var (covered, total) = ParseCondition((string?)line.Attribute("condition-coverage"));
                        var key = (assembly, fileName, number);
                        if (lines.TryGetValue(key, out var existing))
                        {
                            lines[key] = (
                                Math.Max(existing.Hits, hits),
                                Math.Max(existing.Covered, covered),
                                Math.Max(existing.Total, total));
                        }
                        else
                        {
                            lines[key] = (hits, covered, total);
                        }
                    }
                }
            }
        }

        var assemblies = lines
            .GroupBy(static pair => pair.Key.Assembly)
            .Select(static group => new CoverageAssembly(
                group.Key,
                group.Count(),
                group.Count(static pair => pair.Value.Hits > 0),
                group.Sum(static pair => pair.Value.Total),
                group.Sum(static pair => Math.Min(pair.Value.Covered, pair.Value.Total))))
            .OrderBy(static assembly => assembly.Assembly, StringComparer.Ordinal)
            .ToList();

        var uncovered = new List<UncoveredRegion>();
        foreach (var fileGroup in lines.Where(static pair => pair.Value.Hits == 0).GroupBy(static pair => (pair.Key.Assembly, pair.Key.File)))
        {
            var numbers = fileGroup.Select(static pair => pair.Key.Line).Order().ToArray();
            var start = numbers[0];
            var previous = numbers[0];
            for (var i = 1; i <= numbers.Length; i++)
            {
                if (i < numbers.Length && numbers[i] <= previous + 2)
                {
                    previous = numbers[i];
                    continue;
                }

                uncovered.Add(new UncoveredRegion(fileGroup.Key.Assembly, fileGroup.Key.File, start, previous));
                if (i < numbers.Length)
                {
                    start = numbers[i];
                    previous = numbers[i];
                }
            }
        }

        return new CoverageSummary(
            assemblies,
            uncovered.OrderByDescending(static region => region.Lines).ThenBy(static region => region.File, StringComparer.Ordinal).ToList(),
            lines.Keys.Select(static key => key.File).Distinct(StringComparer.Ordinal).Count());
    }

    public static (IReadOnlyList<GeneratorSummary> Generators, IReadOnlyList<KnownDefectResult> Defects) ReadLedgers(string directory)
    {
        var generators = new List<GeneratorSummary>();
        var defects = new Dictionary<(string Id, string Outcome), int>();
        if (!Directory.Exists(directory))
        {
            return (generators, []);
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var schema = String(root, "schema");
                if (schema == "monze.defect.v1")
                {
                    var key = (String(root, "defectId") ?? "unknown", String(root, "outcome") ?? "unknown");
                    defects[key] = defects.TryGetValue(key, out var count) ? count + 1 : 1;
                    continue;
                }

                if (schema != "monze.case.v1" || String(root, "kind") != "summary")
                {
                    continue;
                }

                var outcomes = new Dictionary<string, long>(StringComparer.Ordinal);
                if (root.TryGetProperty("outcomes", out var outcomeObject))
                {
                    foreach (var outcome in outcomeObject.EnumerateObject())
                    {
                        outcomes[outcome.Name] = outcome.Value.GetInt64();
                    }
                }

                var pairwise = root.TryGetProperty("pairwise", out var pairs) ? pairs : default;
                generators.Add(new GeneratorSummary(
                    String(root, "suite") ?? "unknown",
                    String(root, "testId") ?? "unknown",
                    root.TryGetProperty("total", out var total) ? total.GetInt64() : 0,
                    root.TryGetProperty("distinctInputs", out var distinct) ? distinct.GetInt64() : 0,
                    outcomes,
                    pairwise.ValueKind == JsonValueKind.Object && pairwise.TryGetProperty("required", out var required) ? required.GetInt32() : 0,
                    pairwise.ValueKind == JsonValueKind.Object && pairwise.TryGetProperty("covered", out var covered) ? covered.GetInt32() : 0,
                    pairwise.ValueKind == JsonValueKind.Object && pairwise.TryGetProperty("missing", out var missing)
                        ? missing.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToList()
                        : [],
                    root.TryGetProperty("failures", out var failures)
                        ? failures.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToList()
                        : [],
                    root.TryGetProperty("requested", out var requestedCount) && requestedCount.ValueKind == JsonValueKind.Number
                        ? requestedCount.GetInt64()
                        : null));
            }
        }

        return (
            generators,
            defects.Select(static pair => new KnownDefectResult(pair.Key.Id, pair.Key.Outcome, pair.Value))
                .OrderBy(static result => result.DefectId, StringComparer.Ordinal)
                .ToList());
    }

    public static IReadOnlyList<BenchmarkResult> ReadBenchmarks(string directory)
    {
        var results = new List<BenchmarkResult>();
        if (!Directory.Exists(directory))
        {
            return results;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*-report-full*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("Benchmarks", out var benchmarks))
            {
                continue;
            }

            foreach (var benchmark in benchmarks.EnumerateArray())
            {
                var mean = benchmark.TryGetProperty("Statistics", out var statistics)
                    && statistics.ValueKind == JsonValueKind.Object
                    && statistics.TryGetProperty("Mean", out var meanValue)
                        ? meanValue.GetDouble()
                        : double.NaN;
                double? allocated = benchmark.TryGetProperty("Memory", out var memory)
                    && memory.ValueKind == JsonValueKind.Object
                    && memory.TryGetProperty("BytesAllocatedPerOperation", out var bytes)
                        ? bytes.GetDouble()
                        : null;
                results.Add(new BenchmarkResult(
                    String(benchmark, "Type") ?? "unknown",
                    String(benchmark, "Method") ?? "unknown",
                    String(benchmark, "Parameters") ?? string.Empty,
                    mean,
                    allocated));
            }
        }

        return results;
    }

    public static IReadOnlyList<ArtifactDocument> ReadArtifacts(string rawDirectory, params string[] kinds)
    {
        var documents = new List<ArtifactDocument>();
        foreach (var kind in kinds)
        {
            var directory = Path.Combine(rawDirectory, kind);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                documents.Add(new ArtifactDocument(
                    kind,
                    Path.GetRelativePath(directory, file).Replace('\\', '/'),
                    document.RootElement.Clone()));
            }
        }

        return documents;
    }

    public static IReadOnlyList<BrowserCase> ReadBrowserLedger(string? path)
    {
        var cases = new List<BrowserCase>();
        if (path is null || !File.Exists(path))
        {
            return cases;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("cases", out var array))
        {
            return cases;
        }

        foreach (var item in array.EnumerateArray())
        {
            var id = String(item, "test_id");
            if (id is null)
            {
                continue;
            }

            string? priority = null;
            var definition = String(item, "definition");
            if (definition is not null)
            {
                var cells = definition.Split('|', StringSplitOptions.TrimEntries);
                priority = cells.Length > 2 ? cells[2] : null;
            }

            cases.Add(new BrowserCase(id, String(item, "status") ?? "UNKNOWN", priority));
        }

        return cases;
    }

    private static string MethodName(string testName)
    {
        var parenthesis = testName.IndexOf('(');
        return parenthesis > 0 ? testName[..parenthesis] : testName;
    }

    private static string NormalizeSourcePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var index = normalized.IndexOf("/Monze/", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? normalized[(index + "/Monze/".Length)..] : normalized;
    }

    private static (int Covered, int Total) ParseCondition(string? value)
    {
        if (value is null)
        {
            return (0, 0);
        }

        var match = ConditionPattern().Match(value);
        return match.Success
            ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
            : (0, 0);
    }

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static double? Number(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : null;

    private static DateTimeOffset? Date(JsonElement element, string name)
        => String(element, name) is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;

    [GeneratedRegex(@"\((\d+)/(\d+)\)")]
    private static partial Regex ConditionPattern();
}
