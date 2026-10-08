using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

/// <summary>
/// Campaign mode (--campaign-artifact &lt;file&gt;): checks the run against the
/// [ZeroAllocationGate] benchmarks and tests/perf-baseline.json, then writes a
/// monze.artifact.v1 "micro" artifact for the campaign report.
/// The verdict fails on a benchmark that did not complete, a gated benchmark
/// that allocates, or an allocation above its baseline. Mean regressions above
/// 1.2 × baseline are flagged in the notes only, because ShortRun means on a
/// workstation are too noisy to gate on.
/// </summary>
internal sealed class MicroBenchmarkGate
{
    private const double MeanRegressionFactor = 1.2;
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private readonly string _artifactPath;
    private readonly string? _baselinePath;
    private readonly bool _updateBaseline;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    private MicroBenchmarkGate(string artifactPath, string? baselinePath, bool updateBaseline)
    {
        _artifactPath = artifactPath;
        _baselinePath = baselinePath;
        _updateBaseline = updateBaseline;
    }

    public static (MicroBenchmarkGate? Gate, string[] BenchmarkArgs) Parse(string[] args)
    {
        string? artifact = null;
        string? baseline = null;
        var update = false;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--campaign-artifact" when i + 1 < args.Length:
                    artifact = args[++i];
                    break;
                case "--baseline" when i + 1 < args.Length:
                    baseline = args[++i];
                    break;
                case "--update-baseline":
                    update = true;
                    break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }

        return (artifact is null ? null : new MicroBenchmarkGate(artifact, baseline, update), rest.ToArray());
    }

    public int Evaluate(IReadOnlyList<Summary> summaries)
    {
        var baseline = ReadBaseline();
        var invariants = new JsonArray();
        var notes = new List<string>();
        var measured = new SortedDictionary<string, (double MeanNs, long? Bytes)>(StringComparer.Ordinal);
        var failed = false;
        var gated = 0;
        var meanRegressions = 0;
        foreach (var report in summaries.SelectMany(static summary => summary.Reports))
        {
            var key = Key(report.BenchmarkCase);
            var isGated = report.BenchmarkCase.Descriptor.WorkloadMethod.IsDefined(typeof(ZeroAllocationGateAttribute), false);
            if (!report.Success || report.ResultStatistics is null)
            {
                invariants.Add(Invariant($"MICRO-RUN {key}", "benchmark completes", "did not complete", false));
                failed = true;
                continue;
            }

            var mean = report.ResultStatistics.Mean;
            long? bytes = report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase);
            measured[key] = (mean, bytes);
            if (isGated)
            {
                gated++;
                var pass = bytes == 0;
                invariants.Add(Invariant($"MICRO-ZERO {key}", "0 B/op", BytesText(bytes), pass));
                failed |= !pass;
            }

            if (!baseline.TryGetValue(key, out var reference))
            {
                notes.Add($"{key}: chưa có trong baseline");
                continue;
            }

            if (!isGated)
            {
                var pass = bytes is { } allocated && allocated <= reference.Bytes;
                invariants.Add(Invariant($"MICRO-ALLOC {key}", $"<= {reference.Bytes} B/op (baseline)", BytesText(bytes), pass));
                failed |= !pass;
            }

            if (mean > reference.MeanNs * MeanRegressionFactor)
            {
                meanRegressions++;
                notes.Add(string.Create(
                    Culture,
                    $"{key}: mean {mean:0.###} ns > {MeanRegressionFactor} × baseline {reference.MeanNs:0.###} ns"));
            }
        }

        var verdict = measured.Count == 0 && !failed ? "BLOCKED" : failed ? "FAIL" : "PASS";
        if (measured.Count == 0)
        {
            notes.Insert(0, "Không có benchmark nào chạy (kiểm tra --filter).");
        }

        if (_updateBaseline && _baselinePath is not null && !failed)
        {
            WriteBaseline(baseline, measured);
            notes.Add($"Baseline đã cập nhật: {measured.Count} benchmark.");
        }

        var artifact = new JsonObject
        {
            ["schema"] = "monze.artifact.v1",
            ["kind"] = "micro",
            ["id"] = "micro",
            ["title"] = "Micro benchmark (BenchmarkDotNet ShortRun)",
            ["verdict"] = verdict,
            ["metrics"] = new JsonObject
            {
                ["benchmarks"] = measured.Count,
                ["zeroAllocationGates"] = gated,
                ["baselineEntries"] = baseline.Count,
                ["meanRegressions"] = meanRegressions
            },
            ["invariants"] = invariants,
            ["defectIds"] = new JsonArray(),
            ["notes"] = string.Join("; ", notes),
            ["durationMinutes"] = Math.Round(_elapsed.Elapsed.TotalMinutes, 2)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_artifactPath))!);
        File.WriteAllText(_artifactPath, artifact.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return verdict == "PASS" ? 0 : 1;
    }

    private static string Key(BenchmarkCase benchmark)
    {
        var name = $"{benchmark.Descriptor.Type.Name}.{benchmark.Descriptor.WorkloadMethod.Name}";
        var parameters = benchmark.Parameters.DisplayInfo;
        return string.IsNullOrEmpty(parameters) ? name : $"{name} {parameters}";
    }

    private static JsonObject Invariant(string id, string expected, string observed, bool pass)
        => new()
        {
            ["id"] = id,
            ["expected"] = expected,
            ["observed"] = observed,
            ["pass"] = pass
        };

    private static string BytesText(long? bytes)
        => bytes is { } value ? $"{value.ToString(Culture)} B/op" : "không đo được";

    private Dictionary<string, (double MeanNs, long Bytes)> ReadBaseline()
    {
        var entries = new Dictionary<string, (double MeanNs, long Bytes)>(StringComparer.Ordinal);
        if (_baselinePath is null || !File.Exists(_baselinePath))
        {
            return entries;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(_baselinePath));
        foreach (var entry in document.RootElement.GetProperty("benchmarks").EnumerateObject())
        {
            entries[entry.Name] = (
                entry.Value.GetProperty("meanNs").GetDouble(),
                entry.Value.GetProperty("allocatedBytes").GetInt64());
        }

        return entries;
    }

    private void WriteBaseline(
        Dictionary<string, (double MeanNs, long Bytes)> previous,
        SortedDictionary<string, (double MeanNs, long? Bytes)> measured)
    {
        var merged = new SortedDictionary<string, (double MeanNs, long Bytes)>(previous, StringComparer.Ordinal);
        foreach (var (key, value) in measured)
        {
            if (value.Bytes is { } bytes)
            {
                merged[key] = (value.MeanNs, bytes);
            }
        }

        var benchmarks = new JsonObject();
        foreach (var (key, value) in merged)
        {
            benchmarks[key] = new JsonObject
            {
                ["meanNs"] = Math.Round(value.MeanNs, 3),
                ["allocatedBytes"] = value.Bytes
            };
        }

        var document = new JsonObject
        {
            ["schema"] = "monze.perf-baseline.v1",
            ["recordedUtc"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Culture),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["processorCount"] = Environment.ProcessorCount,
            ["job"] = "ShortRun",
            ["benchmarks"] = benchmarks
        };
        File.WriteAllText(
            _baselinePath!,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n");
    }
}
