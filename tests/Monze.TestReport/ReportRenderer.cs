using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Monze.TestReport;

/// <summary>Renders the single campaign report (sections 0–14) as Markdown.</summary>
internal sealed class ReportRenderer
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly CampaignModel _model;
    private readonly Redactor _redactor;
    private readonly IReadOnlyList<GateResult> _gates;
    private readonly string _overall;
    private readonly string _rawDirectory;

    public ReportRenderer(CampaignModel model, Redactor redactor, IReadOnlyList<GateResult> gates, string overall, string rawDirectory)
    {
        _model = model;
        _redactor = redactor;
        _gates = gates;
        _overall = overall;
        _rawDirectory = rawDirectory;
    }

    public string Render()
    {
        var md = new StringBuilder(64 * 1024);
        var manifest = _model.Manifest;
        md.AppendLine($"# Monze — báo cáo chiến dịch kiểm thử tự động `{Text(manifest.CampaignId)}`");
        md.AppendLine();
        md.AppendLine($"> **Verdict: {_overall}** · profile `{Text(manifest.Profile)}` · {manifest.StartedUtc:yyyy-MM-dd HH:mm} UTC");
        md.AppendLine();
        md.AppendLine("Đây là báo cáo duy nhất của lần chạy. Mọi số liệu được dựng tự động từ artifact trong `raw/` (không commit). Báo cáo không chứa secret, DSN hay ID thô.");
        md.AppendLine();

        Section1Environment(md);
        Section2Scope(md);
        Section3Totals(md);
        Section4Functional(md);
        Section5Generated(md);
        Section6Coverage(md);
        Section7Traceability(md);
        Section8Performance(md);
        Section9Chaos(md);
        Section10Defects(md);
        Section11NotRun(md);
        Section12Cleanup(md);
        Section13Verdict(md);
        Section14Appendix(md);
        return md.ToString();
    }

    private void Section1Environment(StringBuilder md)
    {
        var manifest = _model.Manifest;
        md.AppendLine("## 1. Môi trường và commit");
        md.AppendLine();
        md.AppendLine("| Mục | Giá trị |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Commit | `{Text(manifest.Commit)}`{(manifest.Dirty ? " (working tree có thay đổi chưa commit)" : string.Empty)} |");
        md.AppendLine($"| Branch | `{Text(manifest.Branch)}` |");
        md.AppendLine($"| Bắt đầu / kết thúc (UTC) | {manifest.StartedUtc:yyyy-MM-dd HH:mm:ss} / {(manifest.FinishedUtc is { } finished ? finished.ToString("yyyy-MM-dd HH:mm:ss", Invariant) : "chưa kết thúc")} |");
        if (manifest.FinishedUtc is { } end)
        {
            md.AppendLine($"| Thời lượng | {Duration((end - manifest.StartedUtc).TotalSeconds)} |");
        }

        foreach (var pair in manifest.Environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            md.AppendLine($"| {Cell(pair.Key)} | {Cell(pair.Value)} |");
        }

        md.AppendLine();
    }

    private void Section2Scope(StringBuilder md)
    {
        md.AppendLine("## 2. Ma trận phạm vi");
        md.AppendLine();
        md.AppendLine("| Tier | Trạng thái | Thời lượng | Ghi chú |");
        md.AppendLine("|---|---|---|---|");
        foreach (var tier in _model.Manifest.Tiers)
        {
            md.AppendLine($"| {Cell(tier.Name)} | {Badge(tier.Status)} | {Duration(tier.DurationSeconds)} | {Cell(tier.Note ?? string.Empty)} |");
        }

        if (_model.Manifest.Tiers.Count == 0)
        {
            md.AppendLine("| — | NOT_RUN | — | Orchestrator chưa ghi tier nào |");
        }

        md.AppendLine();
    }

    private void Section3Totals(StringBuilder md)
    {
        md.AppendLine("## 3. Tổng hợp theo tier");
        md.AppendLine();
        md.AppendLine("| Nhóm | Test/case | Pass | Fail | Skip | Thời lượng |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var group in _model.TestProjects.GroupBy(static project => TierOf(project.Project)).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            md.AppendLine($"| {Cell(group.Key)} (xUnit) | {N(group.Sum(static p => p.Total))} | {N(group.Sum(static p => p.Passed))} | {N(group.Sum(static p => p.Failed))} | {N(group.Sum(static p => p.Skipped))} | {Duration(group.Sum(static p => p.DurationSeconds))} |");
        }

        if (_model.Generators.Count > 0)
        {
            var total = _model.Generators.Sum(static generator => generator.Total);
            var failed = _model.Generators.Sum(static generator => generator.Outcomes.TryGetValue("fail", out var value) ? value : 0);
            md.AppendLine($"| Case sinh tự động (ledger) | {N(total)} | {N(total - failed)} | {N(failed)} | 0 | — |");
        }

        var artifactsByKind = _model.Artifacts.GroupBy(static artifact => artifact.Kind);
        foreach (var group in artifactsByKind.OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            var verdicts = group.Select(static artifact => Verdict(artifact.Root)).ToList();
            md.AppendLine($"| {Cell(group.Key)} (kịch bản) | {N(verdicts.Count)} | {N(verdicts.Count(static v => v == "PASS"))} | {N(verdicts.Count(static v => v is "FAIL" or "KNOWN_GAP"))} | {N(verdicts.Count(static v => v is "BLOCKED" or "NOT_RUN"))} | — |");
        }

        var grand = _model.TestProjects.Sum(static p => (long)p.Total) + _model.Generators.Sum(static g => g.Total) + _model.Artifacts.Count;
        md.AppendLine();
        md.AppendLine($"**Tổng số case đã thực thi: {N(grand)}** (test xUnit + case sinh tự động + kịch bản perf/chaos).");
        md.AppendLine();
    }

    private void Section4Functional(StringBuilder md)
    {
        md.AppendLine("## 4. Kết quả chức năng");
        md.AppendLine();
        if (_model.TestProjects.Count == 0)
        {
            md.AppendLine("Chưa có kết quả TRX.");
            md.AppendLine();
            return;
        }

        md.AppendLine("| Project | Tổng | Pass | Fail | Skip |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var project in _model.TestProjects)
        {
            md.AppendLine($"| {Cell(project.Project)} | {N(project.Total)} | {N(project.Passed)} | {N(project.Failed)} | {N(project.Skipped)} |");
        }

        md.AppendLine();
        var failures = _model.TestProjects.SelectMany(static project => project.Failures).ToList();
        if (failures.Count > 0)
        {
            md.AppendLine("### Test fail");
            md.AppendLine();
            md.AppendLine("| Project | Test | Thông điệp |");
            md.AppendLine("|---|---|---|");
            foreach (var failure in failures.Take(200))
            {
                md.AppendLine($"| {Cell(failure.Project)} | {Cell(failure.Name)} | {Cell(Truncate(failure.Message ?? string.Empty, 300))} |");
            }

            md.AppendLine();
        }

        var skipped = _model.TestProjects.SelectMany(static project => project.SkippedTests).ToList();
        if (skipped.Count > 0)
        {
            md.AppendLine($"### Test bị skip ({N(skipped.Count)})");
            md.AppendLine();
            foreach (var group in skipped.GroupBy(static test => test.Project))
            {
                md.AppendLine($"- {Cell(group.Key)}: {N(group.Count())} test (thiếu hạ tầng hoặc bị tắt có chủ đích).");
            }

            md.AppendLine();
        }
    }

    private void Section5Generated(StringBuilder md)
    {
        md.AppendLine("## 5. Case sinh tự động (property/generative)");
        md.AppendLine();
        if (_model.Generators.Count == 0)
        {
            md.AppendLine("Chưa có ledger case sinh tự động.");
            md.AppendLine();
            return;
        }

        md.AppendLine("| Generator | Số check | Input khác nhau | Pass | Fail | Pairwise |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var generator in _model.Generators.OrderBy(static g => g.TestId, StringComparer.Ordinal))
        {
            var pass = generator.Outcomes.TryGetValue("pass", out var passed) ? passed : 0;
            var fail = generator.Outcomes.TryGetValue("fail", out var failed) ? failed : 0;
            var pairwise = generator.PairsRequired == 0
                ? "—"
                : $"{generator.PairsCovered}/{generator.PairsRequired} ({Percent(generator.PairsRequired == 0 ? 0 : (double)generator.PairsCovered / generator.PairsRequired)})";
            md.AppendLine($"| {Cell(generator.Suite)}/{Cell(generator.TestId)} | {N(generator.Total)} | {N(generator.DistinctInputs)} | {N(pass)} | {N(fail)} | {pairwise} |");
        }

        var total = _model.Generators.Sum(static g => g.Total);
        md.AppendLine();
        md.AppendLine($"**Tổng: {N(total)} check trên {N(_model.Generators.Count)} generator.**");
        md.AppendLine();
        var failing = _model.Generators.Where(static g => g.Failures.Count > 0).ToList();
        if (failing.Count > 0)
        {
            md.AppendLine("### Counterexample");
            md.AppendLine();
            foreach (var generator in failing)
            {
                foreach (var failure in generator.Failures.Take(5))
                {
                    md.AppendLine($"- `{Text(generator.TestId)}`: {Cell(Truncate(failure, 300))}");
                }
            }

            md.AppendLine();
        }

        var missing = _model.Generators.Where(static g => g.MissingPairs.Count > 0).ToList();
        if (missing.Count > 0)
        {
            md.AppendLine("### Cặp chiều chưa phủ");
            md.AppendLine();
            foreach (var generator in missing)
            {
                md.AppendLine($"- `{Text(generator.TestId)}`: {Cell(string.Join(", ", generator.MissingPairs.Take(10)))}");
            }

            md.AppendLine();
        }
    }

    private void Section6Coverage(StringBuilder md)
    {
        md.AppendLine("## 6. Coverage");
        md.AppendLine();
        var coverage = _model.Coverage;
        if (coverage is null)
        {
            md.AppendLine("Chưa có dữ liệu coverage.");
            md.AppendLine();
            return;
        }

        md.AppendLine("| Assembly | Line | Branch | Dòng phủ/tổng | Nhánh phủ/tổng |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var assembly in coverage.Assemblies)
        {
            md.AppendLine($"| {Cell(assembly.Assembly)} | {Percent(assembly.LineRate)} | {Percent(assembly.BranchRate)} | {N(assembly.LinesCovered)}/{N(assembly.LinesValid)} | {N(assembly.BranchesCovered)}/{N(assembly.BranchesValid)} |");
        }

        var linesValid = coverage.Assemblies.Sum(static a => a.LinesValid);
        var linesCovered = coverage.Assemblies.Sum(static a => a.LinesCovered);
        var branchesValid = coverage.Assemblies.Sum(static a => a.BranchesValid);
        var branchesCovered = coverage.Assemblies.Sum(static a => a.BranchesCovered);
        md.AppendLine($"| **Tổng** | **{Percent(linesValid == 0 ? 0 : (double)linesCovered / linesValid)}** | **{Percent(branchesValid == 0 ? 0 : (double)branchesCovered / branchesValid)}** | {N(linesCovered)}/{N(linesValid)} | {N(branchesCovered)}/{N(branchesValid)} |");
        md.AppendLine();
        md.AppendLine($"Gộp từ mọi file cobertura của lần chạy theo (assembly, file, dòng); nhánh gộp là xấp xỉ. {N(coverage.SourceFiles)} file nguồn.");
        md.AppendLine();
        if (coverage.Uncovered.Count > 0)
        {
            md.AppendLine("### 50 vùng chưa phủ lớn nhất");
            md.AppendLine();
            md.AppendLine("| Assembly | File | Dòng | Số dòng |");
            md.AppendLine("|---|---|---|---|");
            foreach (var region in coverage.Uncovered.Take(50))
            {
                md.AppendLine($"| {Cell(region.Assembly)} | {Cell(region.File)} | {region.StartLine}–{region.EndLine} | {region.Lines} |");
            }

            md.AppendLine();
        }
    }

    private void Section7Traceability(StringBuilder md)
    {
        md.AppendLine("## 7. Traceability");
        md.AppendLine();
        var traces = _model.Artifacts.Where(static artifact => artifact.Kind == "traceability").ToList();
        if (traces.Count == 0)
        {
            md.AppendLine("Tier inventory/traceability chưa chạy trong lần này.");
        }
        else
        {
            md.AppendLine("| Nhóm | Tổng | Đã map | Đã quan sát | Exclusion |");
            md.AppendLine("|---|---|---|---|---|");
            foreach (var trace in traces)
            {
                if (trace.Root.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                {
                    foreach (var group in groups.EnumerateArray())
                    {
                        md.AppendLine($"| {Cell(Str(group, "name"))} | {Num(group, "total")} | {Num(group, "mapped")} | {Num(group, "observed")} | {Num(group, "excluded")} |");
                    }
                }
            }

            foreach (var trace in traces)
            {
                TraceabilityDetails(md, trace.Root);
            }
        }

        md.AppendLine();
        if (_model.BrowserCases.Count > 0)
        {
            md.AppendLine("### 193 case browser (live)");
            md.AppendLine();
            md.AppendLine("| Trạng thái | Số case |");
            md.AppendLine("|---|---|");
            foreach (var group in _model.BrowserCases.GroupBy(static c => c.Status).OrderBy(static g => g.Key, StringComparer.Ordinal))
            {
                md.AppendLine($"| {Cell(group.Key)} | {N(group.Count())} |");
            }

            md.AppendLine();
        }
    }

    private void TraceabilityDetails(StringBuilder md, JsonElement trace)
    {
        if (trace.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object)
        {
            md.AppendLine();
            md.AppendLine($"Inventory {Num(metrics, "inventoryItems")} mục: {Num(metrics, "declared")} đã khai báo, {Num(metrics, "undeclared")} chưa khai báo, {Num(metrics, "mappedItems")} có test ({(metrics.TryGetProperty("mappedPercent", out var percent) ? MetricValue(percent) : "—")}%). Requirement có test: {Num(metrics, "requirementsWithTests")}/{Num(metrics, "requirements")}.");
        }

        if (trace.TryGetProperty("requirements", out var requirements) && requirements.ValueKind == JsonValueKind.Array)
        {
            var missing = requirements.EnumerateArray()
                .Where(static requirement => requirement.TryGetProperty("tests", out var tests) && tests.ValueKind == JsonValueKind.Number && tests.GetInt32() == 0)
                .Select(static requirement => $"{Str(requirement, "id")} ({Str(requirement, "title")})")
                .ToList();
            if (missing.Count > 0)
            {
                md.AppendLine();
                md.AppendLine($"Requirement chưa có test: {Cell(string.Join("; ", missing))}");
            }
        }

        if (trace.TryGetProperty("legacy", out var legacy) && legacy.ValueKind == JsonValueKind.Array && legacy.GetArrayLength() > 0)
        {
            md.AppendLine();
            md.AppendLine($"### {N(legacy.GetArrayLength())} TEST-ID của capability-inventory.json");
            md.AppendLine();
            md.AppendLine("| TEST-ID | Requirement | Số test |");
            md.AppendLine("|---|---|---|");
            foreach (var row in legacy.EnumerateArray())
            {
                var ids = row.TryGetProperty("requirements", out var list) && list.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", list.EnumerateArray().Select(static id => id.GetString()))
                    : string.Empty;
                md.AppendLine($"| {Cell(Str(row, "testId"))} | {Cell(ids)} | {Num(row, "tests")} |");
            }
        }
    }

    private void Section8Performance(StringBuilder md)
    {
        md.AppendLine("## 8. Hiệu năng");
        md.AppendLine();
        md.AppendLine("### 8.1 Micro benchmark");
        md.AppendLine();
        if (_model.Benchmarks.Count == 0)
        {
            md.AppendLine("Chưa chạy.");
        }
        else
        {
            md.AppendLine("| Benchmark | Tham số | Mean | Allocated/op |");
            md.AppendLine("|---|---|---|---|");
            foreach (var benchmark in _model.Benchmarks)
            {
                md.AppendLine($"| {Cell(benchmark.Type)}.{Cell(benchmark.Method)} | {Cell(benchmark.Parameters)} | {Nanoseconds(benchmark.MeanNanoseconds)} | {(benchmark.AllocatedBytes is { } bytes ? $"{bytes.ToString("0", Invariant)} B" : "—")} |");
            }
        }

        md.AppendLine();
        RenderArtifactTable(md, "8.2 Component", "component");
        RenderArtifactTable(md, "8.3 Load toàn tiến trình", "load");
        RenderArtifactTable(md, "8.4 Capacity và backpressure", "capacity");
        RenderArtifactTable(md, "8.5 Soak", "soak");
        RenderArtifactTable(md, "8.6 k6 cross-check", "k6");
    }

    private void Section9Chaos(StringBuilder md)
    {
        md.AppendLine("## 9. Ma trận chaos");
        md.AppendLine();
        RenderArtifactRows(md, "chaos");
    }

    private void Section10Defects(StringBuilder md)
    {
        md.AppendLine("## 10. Defect");
        md.AppendLine();
        var rows = new List<string>();
        foreach (var defect in _model.KnownDefects)
        {
            rows.Add($"| {Cell(defect.DefectId)} | known-defect test | {Cell(defect.Outcome)} | {N(defect.Occurrences)} |");
        }

        foreach (var artifact in _model.Artifacts)
        {
            if (artifact.Root.TryGetProperty("defectIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in ids.EnumerateArray())
                {
                    rows.Add($"| {Cell(id.GetString() ?? string.Empty)} | {Cell(artifact.Kind)}/{Cell(Str(artifact.Root, "id"))} | {Cell(Verdict(artifact.Root))} | 1 |");
                }
            }
        }

        foreach (var failure in _model.TestProjects.SelectMany(static project => project.Failures))
        {
            rows.Add($"| (mới) | {Cell(failure.Project)}: {Cell(failure.Name)} | FAIL | 1 |");
        }

        if (rows.Count == 0)
        {
            md.AppendLine("Không có defect được ghi nhận trong lần chạy.");
        }
        else
        {
            md.AppendLine("| Defect | Nguồn | Kết quả | Số lần |");
            md.AppendLine("|---|---|---|---|");
            foreach (var row in rows)
            {
                md.AppendLine(row);
            }

            md.AppendLine();
            md.AppendLine("`xfail` = lỗi đã biết vẫn tái hiện (test giữ hành vi đúng làm kỳ vọng); `xpass` = lỗi không còn tái hiện, cần gỡ marker cùng bản sửa.");
        }

        md.AppendLine();
    }

    private void Section11NotRun(StringBuilder md)
    {
        md.AppendLine("## 11. Blocked / không chạy");
        md.AppendLine();
        var tiers = _model.Manifest.Tiers.Where(static tier => tier.Status is TierStatus.NotRun or TierStatus.Blocked or TierStatus.Timeout).ToList();
        if (tiers.Count == 0 && _model.Manifest.NotRunReasons.Count == 0)
        {
            md.AppendLine("Mọi tier được yêu cầu đều đã chạy.");
        }

        foreach (var tier in tiers)
        {
            md.AppendLine($"- **{Text(tier.Name)}** — {Text(tier.Status)}: {Cell(tier.Note ?? "không có ghi chú")}");
        }

        foreach (var reason in _model.Manifest.NotRunReasons)
        {
            md.AppendLine($"- {Cell(reason)}");
        }

        md.AppendLine();
    }

    private void Section12Cleanup(StringBuilder md)
    {
        md.AppendLine("## 12. Dọn dẹp và an toàn dữ liệu");
        md.AppendLine();
        var cleanupPath = Path.Combine(_rawDirectory, "cleanup.json");
        if (File.Exists(cleanupPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(cleanupPath));
            md.AppendLine("| Kiểm tra | Kết quả |");
            md.AppendLine("|---|---|");
            foreach (var property in document.RootElement.EnumerateObject())
            {
                md.AppendLine($"| {Cell(property.Name)} | {Cell(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText())} |");
            }
        }
        else
        {
            md.AppendLine("Chưa có `cleanup.json`.");
        }

        md.AppendLine();
        var raw = _model.RawRedactionFindings;
        md.AppendLine(raw.Count == 0
            ? "Quét redaction trên `raw/`: không có phát hiện."
            : $"Quét redaction trên `raw/` (không commit): {N(raw.Count)} phát hiện ({string.Join(", ", raw.GroupBy(static f => f.Rule).Select(static g => $"{g.Key}: {g.Count()}"))}). Chi tiết vị trí ở `raw/redaction/redaction-report.json`.");
        md.AppendLine();
    }

    private void Section13Verdict(StringBuilder md)
    {
        md.AppendLine("## 13. Verdict G0–G4");
        md.AppendLine();
        md.AppendLine("| Gate | Nội dung | Verdict | Bằng chứng |");
        md.AppendLine("|---|---|---|---|");
        foreach (var gate in _gates)
        {
            md.AppendLine($"| {gate.Gate} | {Cell(gate.Title)} | **{gate.Verdict}** | {Cell(gate.Evidence)} |");
        }

        md.AppendLine();
        md.AppendLine($"**Kết luận: {_overall}.** G4 chỉ READY khi load V3 đạt, có soak đủ 2 giờ và có bằng chứng live.");
        md.AppendLine();
    }

    private void Section14Appendix(StringBuilder md)
    {
        md.AppendLine("## 14. Phụ lục");
        md.AppendLine();
        md.AppendLine("### Lệnh đã chạy");
        md.AppendLine();
        foreach (var tier in _model.Manifest.Tiers.Where(static tier => !string.IsNullOrWhiteSpace(tier.Command)))
        {
            md.AppendLine($"- `{Text(tier.Name)}`: `{Text(tier.Command!)}` (exit {tier.ExitCode?.ToString(Invariant) ?? "—"})");
        }

        md.AppendLine();
        md.AppendLine("### Chỉ mục artifact");
        md.AppendLine();
        if (!Directory.Exists(_rawDirectory))
        {
            md.AppendLine("Không có thư mục `raw/`.");
            return;
        }

        var files = Directory.EnumerateFiles(_rawDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        md.AppendLine($"{N(files.Count)} file trong `raw/`.");
        md.AppendLine();
        md.AppendLine("| File | Kích thước | SHA-256 (16 ký tự đầu) |");
        md.AppendLine("|---|---|---|");
        foreach (var file in files.Take(200))
        {
            var info = new FileInfo(file);
            using var stream = File.OpenRead(file);
            var hash = Convert.ToHexString(SHA256.HashData(stream))[..16].ToLowerInvariant();
            md.AppendLine($"| {Cell(Path.GetRelativePath(_rawDirectory, file).Replace('\\', '/'))} | {N(info.Length)} B | `{hash}` |");
        }

        if (files.Count > 200)
        {
            md.AppendLine($"| … | {N(files.Count - 200)} file khác | |");
        }
    }

    private void RenderArtifactTable(StringBuilder md, string title, string kind)
    {
        md.AppendLine($"### {title}");
        md.AppendLine();
        RenderArtifactRows(md, kind);
    }

    private void RenderArtifactRows(StringBuilder md, string kind)
    {
        var artifacts = _model.Artifacts.Where(artifact => artifact.Kind == kind).ToList();
        if (artifacts.Count == 0)
        {
            md.AppendLine("Chưa chạy.");
            md.AppendLine();
            return;
        }

        md.AppendLine("| ID | Mô tả | Verdict | Chỉ số chính | Invariant | Defect |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var artifact in artifacts.OrderBy(static artifact => Str(artifact.Root, "id"), StringComparer.Ordinal))
        {
            var root = artifact.Root;
            var metrics = root.TryGetProperty("metrics", out var metricObject) && metricObject.ValueKind == JsonValueKind.Object
                ? string.Join("; ", metricObject.EnumerateObject().Take(6).Select(static property => $"{property.Name}={MetricValue(property.Value)}"))
                : string.Empty;
            var invariants = root.TryGetProperty("invariants", out var invariantArray) && invariantArray.ValueKind == JsonValueKind.Array
                ? $"{invariantArray.EnumerateArray().Count(static item => item.TryGetProperty("pass", out var pass) && pass.ValueKind == JsonValueKind.True)}/{invariantArray.GetArrayLength()}"
                : "—";
            var defects = root.TryGetProperty("defectIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? string.Join(", ", ids.EnumerateArray().Select(static id => id.GetString()))
                : string.Empty;
            md.AppendLine($"| {Cell(Str(root, "id"))} | {Cell(Str(root, "title"))} | {Badge(Verdict(root))} | {Cell(metrics)} | {invariants} | {Cell(defects)} |");
        }

        md.AppendLine();
    }

    private static string MetricValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var integer)
                ? integer.ToString("N0", Invariant)
                : value.GetDouble().ToString("0.###", Invariant),
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText()
        };

    private static string Verdict(JsonElement root) => Str(root, "verdict") is { Length: > 0 } verdict ? verdict : "UNKNOWN";

    private static string Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Num(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64().ToString("N0", Invariant)
            : "—";

    private static string TierOf(string trxName)
    {
        var dot = trxName.IndexOf('.');
        return dot > 0 ? trxName[..dot] : trxName;
    }

    private static string Badge(string status) => status switch
    {
        "PASS" => "✅ PASS",
        "FAIL" => "❌ FAIL",
        "KNOWN_GAP" => "⚠️ KNOWN_GAP",
        "KNOWN_GAP_NOT_REPRODUCED" => "🔁 KNOWN_GAP_NOT_REPRODUCED",
        "BLOCKED" => "⛔ BLOCKED",
        "TIMEOUT" => "⏱️ TIMEOUT",
        "ERROR" => "💥 ERROR",
        "NOT_RUN" => "— NOT_RUN",
        _ => status
    };

    private string Text(string value) => _redactor.Scrub(value).Replace('`', '\'');

    private string Cell(string value)
        => _redactor.Scrub(value).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "…";

    private static string N(long value) => value.ToString("N0", Invariant);

    private static string Percent(double rate) => (rate * 100).ToString("0.0", Invariant) + "%";

    private static string Duration(double seconds)
        => seconds <= 0
            ? "—"
            : seconds < 60
                ? $"{seconds.ToString("0.0", Invariant)} s"
                : TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss", Invariant);

    private static string Nanoseconds(double value)
        => double.IsNaN(value)
            ? "—"
            : value < 1_000
                ? $"{value.ToString("0.00", Invariant)} ns"
                : value < 1_000_000
                    ? $"{(value / 1_000).ToString("0.00", Invariant)} µs"
                    : $"{(value / 1_000_000).ToString("0.00", Invariant)} ms";
}
