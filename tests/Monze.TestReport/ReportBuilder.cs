using System.Text;
using System.Text.Json;

namespace Monze.TestReport;

internal sealed record ReportBuildResult(int ExitCode, string ReportPath, IReadOnlyList<RedactionFinding> ReportFindings, int RawFindings);

/// <summary>
/// Reads every artifact of one campaign directory and writes the single
/// REPORT.md plus campaign-summary.json next to raw/.
/// </summary>
internal static class ReportBuilder
{
    public const int Success = 0;
    public const int BuilderError = 3;
    public const int RedactionViolation = 4;

    private static readonly string[] ArtifactKinds = ["component", "load", "capacity", "soak", "k6", "chaos", "traceability", "micro"];
    private static readonly UTF8Encoding Utf8 = new(false);

    public static ReportBuildResult Build(string campaignDirectory, string? repositoryRoot, string? secretsFile, string? browserLedger)
    {
        var raw = Path.Combine(campaignDirectory, "raw");
        var reportPath = Path.Combine(campaignDirectory, "REPORT.md");
        var redactor = Redactor.FromSources(repositoryRoot, secretsFile);

        var rawFindings = ScanRaw(raw, redactor);
        var (generators, defects) = ArtifactReaders.ReadLedgers(Path.Combine(raw, "ledgers"));
        var model = new CampaignModel(
            ArtifactReaders.ReadManifest(Path.Combine(raw, "manifest.json")),
            ArtifactReaders.ReadTrx(Path.Combine(raw, "trx")),
            ArtifactReaders.ReadCoverage(Path.Combine(raw, "coverage")),
            generators,
            defects,
            ArtifactReaders.ReadBenchmarks(Path.Combine(raw, "bdn")),
            ArtifactReaders.ReadArtifacts(raw, ArtifactKinds),
            ArtifactReaders.ReadBrowserLedger(browserLedger),
            rawFindings);

        var gates = GateEvaluator.Evaluate(model, redactionViolated: false);
        var overall = GateEvaluator.Overall(gates, model);
        var markdown = new ReportRenderer(model, redactor, gates, overall, raw).Render();
        var summary = JsonSerializer.Serialize(Summarize(model, gates, overall), new JsonSerializerOptions { WriteIndented = true });
        var scrubbedSummary = redactor.Scrub(summary);

        var findings = redactor.Scan("REPORT.md", markdown).Concat(redactor.Scan("campaign-summary.json", scrubbedSummary)).ToList();
        if (findings.Count > 0)
        {
            // Never write the offending content: replace the report with a
            // safe stub that only names rules and line numbers.
            var stub = new StringBuilder();
            stub.AppendLine($"# Monze — báo cáo chiến dịch `{redactor.Scrub(model.Manifest.CampaignId)}`");
            stub.AppendLine();
            stub.AppendLine("> **Verdict: NOT READY — redaction violation.** Báo cáo đầy đủ không được ghi vì chứa dữ liệu nhạy cảm.");
            stub.AppendLine();
            foreach (var finding in findings.Take(100))
            {
                stub.AppendLine($"- {finding.File}:{finding.Line} — {finding.Rule}");
            }

            File.WriteAllText(reportPath, stub.ToString(), Utf8);
            WriteRedactionReport(raw, findings, rawFindings);
            return new ReportBuildResult(RedactionViolation, reportPath, findings, rawFindings.Count);
        }

        File.WriteAllText(reportPath, markdown, Utf8);
        File.WriteAllText(Path.Combine(campaignDirectory, "campaign-summary.json"), scrubbedSummary, Utf8);
        WriteRedactionReport(raw, findings, rawFindings);
        return new ReportBuildResult(Success, reportPath, findings, rawFindings.Count);
    }

    private static object Summarize(CampaignModel model, IReadOnlyList<GateResult> gates, string overall)
        => new
        {
            schema = "monze.campaign-summary.v1",
            campaignId = model.Manifest.CampaignId,
            profile = model.Manifest.Profile,
            commit = model.Manifest.Commit,
            branch = model.Manifest.Branch,
            dirty = model.Manifest.Dirty,
            startedUtc = model.Manifest.StartedUtc,
            finishedUtc = model.Manifest.FinishedUtc,
            verdict = overall,
            gates = gates.Select(static gate => new { gate.Gate, gate.Verdict }),
            tiers = model.Manifest.Tiers.Select(static tier => new { tier.Name, tier.Status, tier.DurationSeconds }),
            tests = new
            {
                total = model.TestProjects.Sum(static p => p.Total),
                passed = model.TestProjects.Sum(static p => p.Passed),
                failed = model.TestProjects.Sum(static p => p.Failed),
                skipped = model.TestProjects.Sum(static p => p.Skipped)
            },
            generatedChecks = model.Generators.Sum(static g => g.Total),
            generators = model.Generators.Count,
            coverage = model.Coverage?.Assemblies.Select(static a => new
            {
                a.Assembly,
                line = Math.Round(a.LineRate * 100, 2),
                branch = Math.Round(a.BranchRate * 100, 2)
            }),
            knownDefects = model.KnownDefects.Select(static d => new { d.DefectId, d.Outcome }),
            scenarios = model.Artifacts.Select(static artifact => new
            {
                artifact.Kind,
                id = artifact.Root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : artifact.Name,
                verdict = artifact.Root.TryGetProperty("verdict", out var verdict) && verdict.ValueKind == JsonValueKind.String ? verdict.GetString() : null
            }),
            benchmarks = model.Benchmarks.Count
        };

    private static List<RedactionFinding> ScanRaw(string raw, Redactor redactor)
    {
        var findings = new List<RedactionFinding>();
        if (!Directory.Exists(raw))
        {
            return findings;
        }

        foreach (var file in Directory.EnumerateFiles(raw, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".json" or ".jsonl" or ".trx" or ".xml" or ".log" or ".txt" or ".md" or ".csv"))
            {
                continue;
            }

            if (file.Contains(Path.Combine("raw", "redaction"), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(file);
            if (info.Length > 64 * 1024 * 1024)
            {
                continue;
            }

            var relative = Path.GetRelativePath(raw, file).Replace('\\', '/');
            findings.AddRange(redactor.Scan(relative, File.ReadAllText(file))
                .Where(static finding => finding.Rule != "long-id"));
        }

        return findings;
    }

    private static void WriteRedactionReport(string raw, IReadOnlyList<RedactionFinding> reportFindings, IReadOnlyList<RedactionFinding> rawFindings)
    {
        var directory = Path.Combine(raw, "redaction");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new
        {
            schema = "monze.redaction.v1",
            report = reportFindings.Select(static f => new { f.File, f.Line, f.Rule }),
            raw = rawFindings.Select(static f => new { f.File, f.Line, f.Rule })
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "redaction-report.json"), json, Utf8);
    }
}
