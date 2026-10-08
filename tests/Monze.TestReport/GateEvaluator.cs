namespace Monze.TestReport;

internal sealed record GateResult(string Gate, string Title, string Verdict, string Evidence);

/// <summary>
/// Maps tier outcomes to the correctness gates of docs/monze-correctness-test-plan.md.
/// A gate is READY only when every tier it needs ran and passed; a missing tier
/// keeps the default verdict "evidence missing".
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

    private static bool SoakIsFullLength(CampaignModel model)
        => model.Artifacts.Any(static artifact =>
            artifact.Kind == "soak"
            && artifact.Root.TryGetProperty("durationMinutes", out var minutes)
            && minutes.ValueKind == System.Text.Json.JsonValueKind.Number
            && minutes.GetDouble() >= 120);
}
