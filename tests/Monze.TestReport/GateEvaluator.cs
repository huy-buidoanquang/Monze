namespace Monze.TestReport;

internal sealed record GateResult(string Gate, string Title, string Verdict, string Evidence);

/// <summary>
/// Maps tier outcomes to the correctness gates of docs/monze-correctness-test-plan.md.
/// A gate is READY only when every tier it needs ran and passed; a missing tier
/// keeps the default verdict "evidence missing". G1 also fails when an
/// assembly's coverage drops below its floor in tests/coverage-thresholds.json.
/// </summary>
internal static class GateEvaluator
{
    public const string Ready = "READY";
    public const string Failed = "FAILED";
    public const string Missing = "EVIDENCE MISSING";

    private static readonly (string Gate, string Title, string[] Tiers)[] Gates =
    [
        ("G0", "Inventory và hygiene", ["build", "inventory"]),
        ("G1", "Logic xác định (unit, property, E2E offline)", ["unit", "property", "e2e"]),
        ("G2", "PostgreSQL từ zero, migration, concurrency", ["integration"]),
        ("G3", "Fault injection và chaos", ["chaos", "capacity"]),
        ("G4", "Quy mô, soak 2 giờ và bằng chứng live", ["micro", "component", "load", "soak", "live"])
    ];

    // Coverage is collected by these tiers; floors only apply when all of them ran.
    private static readonly string[] CoverageTiers = ["unit", "property", "integration", "e2e"];

    public static IReadOnlyList<GateResult> Evaluate(CampaignModel model, bool redactionViolated)
    {
        var tiers = model.Manifest.Tiers.ToDictionary(static tier => tier.Name, StringComparer.Ordinal);
        var results = new List<GateResult>();
        foreach (var (gate, title, needed) in Gates)
        {
            var states = needed.Select(name => tiers.TryGetValue(name, out var tier) ? (name, tier.Status) : (name, TierStatus.NotRun)).ToList();
            var verdict = states.Any(static state => state.Item2 is TierStatus.Fail or TierStatus.Error or TierStatus.Timeout)
                ? Failed
                : states.All(static state => state.Item2 == TierStatus.Pass)
                    ? Ready
                    : Missing;
            if (gate == "G4" && verdict == Ready && !SoakIsFullLength(model))
            {
                verdict = Missing;
            }

            var evidence = string.Join(", ", states.Select(static state => $"{state.name}={state.Item2}"));
            if (gate == "G1" && CoverageFloorsApply(model) && CoverageBelowFloor(model) is { Count: > 0 } below)
            {
                verdict = Failed;
                evidence += "; coverage dưới ngưỡng: " + string.Join(", ", below);
            }

            results.Add(new GateResult(gate, title, verdict, evidence));
        }

        if (redactionViolated)
        {
            results.Add(new GateResult("SAFETY", "Redaction", Failed, "Báo cáo chứa dữ liệu nhạy cảm"));
        }

        return results;
    }

    public static string Overall(IReadOnlyList<GateResult> gates, CampaignModel model)
    {
        var failedTests = model.TestProjects.Sum(static project => project.Failed) > 0
            || model.Generators.Any(static generator => generator.Outcomes.TryGetValue("fail", out var failed) && failed > 0)
            || model.KnownDefects.Any(static defect => defect.Outcome == "xpass");
        if (failedTests || gates.Any(static gate => gate.Verdict == Failed))
        {
            return "NOT READY — failures";
        }

        return gates.All(static gate => gate.Verdict == Ready)
            ? "READY"
            : "NOT READY — evidence missing";
    }

    /// <summary>Whether the floors can be judged: there are floors and coverage, and every coverage tier ran.</summary>
    public static bool CoverageFloorsApply(CampaignModel model)
    {
        if (model.Coverage is null || model.CoverageThresholds.Count == 0)
        {
            return false;
        }

        var tiers = model.Manifest.Tiers.ToDictionary(static tier => tier.Name, StringComparer.Ordinal);
        return CoverageTiers.All(name => tiers.TryGetValue(name, out var tier) && tier.Status != TierStatus.NotRun);
    }

    /// <summary>"Assembly line|branch x% < floor%" for each measure below its floor (an assembly without coverage is below).</summary>
    public static IReadOnlyList<string> CoverageBelowFloor(CampaignModel model)
    {
        var below = new List<string>();
        foreach (var (assembly, threshold) in model.CoverageThresholds.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var measured = model.Coverage?.Assemblies.FirstOrDefault(a => a.Assembly == assembly);
            var line = measured is null ? 0 : measured.LineRate * 100;
            var branch = measured is null ? 0 : measured.BranchRate * 100;
            if (line < threshold.Line)
            {
                below.Add(FormattableString.Invariant($"{assembly} line {line:0.0}% < {threshold.Line:0.0}%"));
            }

            if (branch < threshold.Branch)
            {
                below.Add(FormattableString.Invariant($"{assembly} branch {branch:0.0}% < {threshold.Branch:0.0}%"));
            }
        }

        return below;
    }

    private static bool SoakIsFullLength(CampaignModel model)
        => model.Artifacts.Any(static artifact =>
            artifact.Kind == "soak"
            && artifact.Root.TryGetProperty("durationMinutes", out var minutes)
            && minutes.ValueKind == System.Text.Json.JsonValueKind.Number
            && minutes.GetDouble() >= 120);
}
