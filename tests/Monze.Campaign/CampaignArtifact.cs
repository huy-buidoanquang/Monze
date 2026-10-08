using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Monze.Testing.Harness;

namespace Monze.Campaign;

/// <summary>
/// One monze.artifact.v1 result (component, load, capacity, chaos or soak)
/// as the campaign report reads it from raw/&lt;kind&gt;/&lt;id&gt;.json. The
/// verdict is FAIL when an invariant fails; KNOWN_GAP when only invariants
/// registered with <see cref="KnownGap"/> fail; KNOWN_GAP_NOT_REPRODUCED when
/// those pass too (the defect may be fixed, so the expectation must change);
/// PASS otherwise, unless a scenario sets BLOCKED.
/// </summary>
public sealed class CampaignArtifact
{
    private readonly JsonObject _metrics = [];
    private readonly JsonArray _invariants = [];
    private readonly JsonArray _defectIds = [];
    private readonly List<string> _notes = [];
    private readonly HashSet<string> _knownGapInvariants = new(StringComparer.Ordinal);
    private string? _verdict;

    public CampaignArtifact(string kind, string id, string title)
    {
        Kind = kind;
        Id = id;
        Title = title;
    }

    public string Kind { get; }

    public string Id { get; }

    public string Title { get; }

    public string Verdict
    {
        get
        {
            if (_verdict is not null)
            {
                return _verdict;
            }

            var failed = _invariants
                .Where(static invariant => !invariant!["pass"]!.GetValue<bool>())
                .Select(static invariant => invariant!["id"]!.GetValue<string>())
                .ToList();
            if (failed.Any(id => !_knownGapInvariants.Contains(id)))
            {
                return "FAIL";
            }

            return failed.Count > 0 ? "KNOWN_GAP" : _knownGapInvariants.Count > 0 ? "KNOWN_GAP_NOT_REPRODUCED" : "PASS";
        }
    }

    public CampaignArtifact Metric(string name, double value)
    {
        _metrics[name] = Math.Round(value, 3);
        return this;
    }

    public CampaignArtifact Metric(string name, long value)
    {
        _metrics[name] = value;
        return this;
    }

    /// <summary>Adds p50/p99/max in milliseconds of a histogram recorded in microseconds.</summary>
    public CampaignArtifact Latency(string prefix, LatencyHistogram histogram)
    {
        var summary = histogram.Summarize();
        Metric(prefix + "P50Ms", summary.P50 / 1000.0);
        Metric(prefix + "P99Ms", summary.P99 / 1000.0);
        return Metric(prefix + "MaxMs", summary.Max / 1000.0);
    }

    public CampaignArtifact Invariant(string id, string expected, string observed, bool pass)
    {
        _invariants.Add(new JsonObject
        {
            ["id"] = id,
            ["expected"] = expected,
            ["observed"] = observed,
            ["pass"] = pass
        });
        return this;
    }

    /// <summary>A p99 bound, in milliseconds, on a histogram recorded in microseconds.</summary>
    public CampaignArtifact P99AtMost(string id, LatencyHistogram histogram, double milliseconds, string source)
    {
        var p99 = histogram.ValueAtPercentile(99) / 1000.0;
        return Invariant(
            id,
            string.Create(CultureInfo.InvariantCulture, $"p99 ≤ {milliseconds:0.###} ms ({source})"),
            string.Create(CultureInfo.InvariantCulture, $"{p99:0.###} ms over {histogram.Count:N0}"),
            histogram.Count > 0 && p99 <= milliseconds);
    }

    /// <summary>Records the <see cref="MonzeInvariants"/> check as one invariant.</summary>
    public CampaignArtifact DbInvariants(IReadOnlyList<InvariantViolation> violations)
        => Invariant(
            "db-invariants",
            "không vi phạm invariant DB",
            violations.Count == 0 ? "0" : string.Join(", ", violations.Select(static violation => $"{violation.Id}={violation.Count}")),
            violations.Count == 0);

    /// <summary>Marks <paramref name="invariantId"/> as the reproduction of known defect <paramref name="defectId"/>.</summary>
    public CampaignArtifact KnownGap(string defectId, string invariantId)
    {
        _knownGapInvariants.Add(invariantId);
        if (!_defectIds.Any(existing => existing!.GetValue<string>() == defectId))
        {
            _defectIds.Add(defectId);
        }

        return this;
    }

    public CampaignArtifact Note(string note)
    {
        _notes.Add(note);
        return this;
    }

    public CampaignArtifact Block(string reason)
    {
        _verdict = "BLOCKED";
        return Note(reason);
    }

    public void Write(string rawDirectory, TimeSpan elapsed)
    {
        var document = new JsonObject
        {
            ["schema"] = "monze.artifact.v1",
            ["kind"] = Kind,
            ["id"] = Id,
            ["title"] = Title,
            ["verdict"] = Verdict,
            ["metrics"] = _metrics.DeepClone(),
            ["invariants"] = _invariants.DeepClone(),
            ["defectIds"] = _defectIds.DeepClone(),
            ["notes"] = string.Join("; ", _notes),
            ["durationMinutes"] = Math.Round(elapsed.TotalMinutes, 2)
        };
        var directory = Path.Combine(rawDirectory, Kind);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, Id.ToLowerInvariant() + ".json"),
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
