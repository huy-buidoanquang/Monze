using System.Text.Json;
using Monze.Testing;
using Monze.TestReport;
using Xunit;

namespace Monze.Tests.CampaignReport;

public sealed class ReportBuilderTests : IDisposable
{
    private const string CanarySecret = "CanaryValue-7f3a9c21-do-not-leak";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "monze-report-test-" + Guid.NewGuid().ToString("N"));

    public ReportBuilderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "raw"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    [Req("REQ-RPT-001")]
    public void Report_has_every_section_and_counts_every_tier()
    {
        WriteManifest(("build", "PASS"), ("unit", "PASS"), ("integration", "NOT_RUN"));
        WriteTrx("unit.Monze.Tests", ("A.B.Passes", "Passed", null), ("A.B.Fails", "Failed", "boom"), ("A.B.Skipped", "NotExecuted", null));
        WriteLedgerSummary("property", "G01", total: 1500, failed: 0);

        var result = Build();

        Assert.Equal(ReportBuilder.Success, result.ExitCode);
        var report = File.ReadAllText(result.ReportPath);
        for (var section = 1; section <= 14; section++)
        {
            Assert.Contains($"## {section}. ", report, StringComparison.Ordinal);
        }

        Assert.Contains("NOT READY — failures", report, StringComparison.Ordinal);
        Assert.Contains("1,500", report, StringComparison.Ordinal);
        Assert.Contains("A.B.Fails", report, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "campaign-summary.json")));
    }

    [Fact]
    [Req("REQ-RPT-002")]
    public void Missing_tiers_keep_the_evidence_missing_verdict()
    {
        WriteManifest(("build", "PASS"), ("inventory", "PASS"), ("unit", "PASS"), ("property", "PASS"), ("e2e", "PASS"), ("integration", "PASS"));
        WriteTrx("unit.Monze.Tests", ("A.B.Passes", "Passed", null));

        var report = File.ReadAllText(Build().ReportPath);

        Assert.Contains("NOT READY — evidence missing", report, StringComparison.Ordinal);
        Assert.Contains("| G2 |", report, StringComparison.Ordinal);
    }

    [Fact]
    [Req("REQ-RPT-003", "REQ-SEC-102")]
    public void Secrets_dsn_and_long_ids_never_reach_the_report()
    {
        var secrets = Path.Combine(_root, "secrets.json");
        File.WriteAllText(secrets, JsonSerializer.Serialize(new { canary = CanarySecret }));
        WriteManifest(("build", "PASS"), ("unit", "FAIL"));
        WriteTrx(
            "unit.Monze.Tests",
            ("A.B.Leaks", "Failed", $"token {CanarySecret} Host=x;Password=hunter2secret; clan 2104288434238525440 Bearer abcdefghijklmnopqrstuvwxyz"));
        WriteArtifact("chaos", "PG-01", "FAIL", title: $"title with {CanarySecret}");

        var result = ReportBuilder.Build(_root, null, secrets, null);

        Assert.True(
            result.ExitCode == ReportBuilder.Success,
            string.Join("; ", result.ReportFindings.Select(static f => $"{f.File}:{f.Line}:{f.Rule}")));
        var report = File.ReadAllText(result.ReportPath);
        Assert.DoesNotContain(CanarySecret, report, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2secret", report, StringComparison.Ordinal);
        Assert.DoesNotContain("2104288434238525440", report, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", report, StringComparison.Ordinal);
        Assert.Contains("[redacted]", report, StringComparison.Ordinal);
        Assert.Contains("[id]", report, StringComparison.Ordinal);
        Assert.True(result.RawFindings > 0);
    }

    [Fact]
    [Req("REQ-RPT-004")]
    public void Coverage_from_several_runs_is_merged_per_line()
    {
        WriteManifest(("unit", "PASS"));
        WriteCobertura("unit", ("Monze.Domain", "src/Monze.Domain/A.cs", 1, 1), ("Monze.Domain", "src/Monze.Domain/A.cs", 2, 0));
        WriteCobertura("e2e", ("Monze.Domain", "src/Monze.Domain/A.cs", 1, 0), ("Monze.Domain", "src/Monze.Domain/A.cs", 2, 3));

        var report = File.ReadAllText(Build().ReportPath);

        Assert.Contains("| Monze.Domain | 100.0% |", report, StringComparison.Ordinal);
    }

    [Fact]
    [Req("REQ-RPT-005")]
    public void Scan_names_the_rule_without_echoing_the_value()
    {
        var redactor = new Redactor([CanarySecret]);

        var findings = redactor.Scan("x.md", $"ok\nleak {CanarySecret}\nPassword=abc\n12345678901234567");

        Assert.Equal(["secret-value", "dsn-credential", "long-id"], findings.Select(static f => f.Rule));
        Assert.All(findings, finding => Assert.DoesNotContain(CanarySecret, finding.Rule, StringComparison.Ordinal));
    }

    [Fact]
    [Req("REQ-RPT-006")]
    public void Traceability_section_lists_groups_requirements_and_legacy_test_ids()
    {
        WriteManifest(("build", "PASS"), ("inventory", "PASS"));
        var directory = Path.Combine(_root, "raw", "traceability");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "trace-map.json"), JsonSerializer.Serialize(new
        {
            schema = "monze.artifact.v1",
            kind = "traceability",
            id = "trace-map",
            title = "Inventory và traceability",
            verdict = "PASS",
            metrics = new { inventoryItems = 3, declared = 3, undeclared = 0, mappedItems = 2, mappedPercent = 66.7, requirements = 2, requirementsWithTests = 1 },
            invariants = Array.Empty<object>(),
            defectIds = Array.Empty<string>(),
            notes = "",
            durationMinutes = 0,
            groups = new[] { new { name = "cmd", total = 3, mapped = 2, observed = 0, excluded = 0 } },
            requirements = new[]
            {
                new { id = "REQ-CMD-001", title = "Định tuyến lệnh", tests = 4 },
                new { id = "REQ-HOST-011", title = "Vòng đời bot", tests = 0 }
            },
            legacy = new[] { new { testId = "CMD-HELP", requirements = new[] { "REQ-HELP-001" }, tests = 15 } }
        }));

        var report = File.ReadAllText(Build().ReportPath);

        Assert.Contains("| cmd | 3 | 2 | 0 | 0 |", report, StringComparison.Ordinal);
        Assert.Contains("Requirement có test: 1/2", report, StringComparison.Ordinal);
        Assert.Contains("REQ-HOST-011 (Vòng đời bot)", report, StringComparison.Ordinal);
        Assert.Contains("| CMD-HELP | REQ-HELP-001 | 15 |", report, StringComparison.Ordinal);
    }

    private ReportBuildResult Build() => ReportBuilder.Build(_root, null, null, null);

    private void WriteManifest(params (string Name, string Status)[] tiers)
    {
        var manifest = new
        {
            campaignId = "test-campaign",
            profile = "quick",
            startedUtc = "2026-10-08T00:00:00Z",
            finishedUtc = "2026-10-08T00:10:00Z",
            commit = "abc1234",
            branch = "fix/browser-test-findings",
            dirty = false,
            environment = new { os = "Windows", dotnet = "10.0.401" },
            tiers = tiers.Select(static tier => new { name = tier.Name, status = tier.Status, durationSeconds = 1.5, exitCode = 0, command = "dotnet test", note = (string?)null }),
            notRun = Array.Empty<string>()
        };
        File.WriteAllText(Path.Combine(_root, "raw", "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private void WriteTrx(string name, params (string Test, string Outcome, string? Message)[] results)
    {
        var directory = Path.Combine(_root, "raw", "trx");
        Directory.CreateDirectory(directory);
        var body = string.Join(
            Environment.NewLine,
            results.Select(static result =>
                $"<UnitTestResult testName=\"{result.Test}\" outcome=\"{result.Outcome}\" duration=\"00:00:00.010\">"
                + (result.Message is null ? string.Empty : $"<Output><ErrorInfo><Message>{System.Security.SecurityElement.Escape(result.Message)}</Message></ErrorInfo></Output>")
                + "</UnitTestResult>"));
        File.WriteAllText(
            Path.Combine(directory, name + ".trx"),
            $"<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>{body}</Results></TestRun>");
    }

    private void WriteLedgerSummary(string suite, string testId, long total, long failed)
    {
        var directory = Path.Combine(_root, "raw", "ledgers");
        Directory.CreateDirectory(directory);
        var line = JsonSerializer.Serialize(new
        {
            schema = "monze.case.v1",
            kind = "summary",
            suite,
            testId,
            total,
            distinctInputs = total,
            outcomes = new Dictionary<string, long> { ["pass"] = total - failed, ["fail"] = failed },
            pairwise = new { required = 4, covered = 4, missing = Array.Empty<string>() },
            failures = Array.Empty<string>()
        });
        File.WriteAllText(Path.Combine(directory, $"{suite}.{testId}.jsonl"), line + Environment.NewLine);
    }

    private void WriteArtifact(string kind, string id, string verdict, string title)
    {
        var directory = Path.Combine(_root, "raw", kind, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            schema = "monze.artifact.v1",
            kind,
            id,
            title,
            verdict,
            metrics = new { rtoSeconds = 3.5 },
            invariants = new[] { new { id = "no-duplicate-send", pass = true } },
            defectIds = new[] { "DEF-07" }
        }));
    }

    private void WriteCobertura(string name, params (string Assembly, string File, int Line, int Hits)[] lines)
    {
        var directory = Path.Combine(_root, "raw", "coverage");
        Directory.CreateDirectory(directory);
        var packages = lines.GroupBy(static line => line.Assembly).Select(static group =>
            $"<package name=\"{group.Key}\"><classes><class name=\"A\" filename=\"{group.First().File}\"><lines>"
            + string.Concat(group.Select(static line => $"<line number=\"{line.Line}\" hits=\"{line.Hits}\" branch=\"False\" />"))
            + "</lines></class></classes></package>");
        File.WriteAllText(
            Path.Combine(directory, name + ".cobertura.xml"),
            $"<coverage><packages>{string.Concat(packages)}</packages></coverage>");
    }
}
