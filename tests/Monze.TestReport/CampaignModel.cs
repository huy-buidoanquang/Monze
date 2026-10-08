namespace Monze.TestReport;

internal static class TierStatus
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string Blocked = "BLOCKED";
    public const string NotRun = "NOT_RUN";
    public const string Timeout = "TIMEOUT";
    public const string Error = "ERROR";
}

internal sealed record TierRecord(
    string Name,
    string Status,
    double DurationSeconds,
    int? ExitCode,
    string? Command,
    string? Note);

internal sealed record CampaignManifest(
    string CampaignId,
    string Profile,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    string Commit,
    string Branch,
    bool Dirty,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<TierRecord> Tiers,
    IReadOnlyList<string> NotRunReasons);

internal sealed record TestResult(string Project, string Name, string Outcome, double DurationSeconds, string? Message);

internal sealed record TestProjectSummary(
    string Project,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    double DurationSeconds,
    IReadOnlyList<TestResult> Failures,
    IReadOnlyList<TestResult> SkippedTests);

internal sealed record CoverageAssembly(
    string Assembly,
    int LinesValid,
    int LinesCovered,
    int BranchesValid,
    int BranchesCovered)
{
    public double LineRate => LinesValid == 0 ? 0 : (double)LinesCovered / LinesValid;
    public double BranchRate => BranchesValid == 0 ? 0 : (double)BranchesCovered / BranchesValid;
}

internal sealed record UncoveredRegion(string Assembly, string File, int StartLine, int EndLine)
{
    public int Lines => EndLine - StartLine + 1;
}

internal sealed record CoverageSummary(
    IReadOnlyList<CoverageAssembly> Assemblies,
    IReadOnlyList<UncoveredRegion> Uncovered,
    int SourceFiles);

internal sealed record GeneratorSummary(
    string Suite,
    string TestId,
    long Total,
    long DistinctInputs,
    IReadOnlyDictionary<string, long> Outcomes,
    int PairsRequired,
    int PairsCovered,
    IReadOnlyList<string> MissingPairs,
    IReadOnlyList<string> Failures);

internal sealed record KnownDefectResult(string DefectId, string Outcome, int Occurrences);

internal sealed record BenchmarkResult(
    string Type,
    string Method,
    string Parameters,
    double MeanNanoseconds,
    double? AllocatedBytes);

internal sealed record ArtifactDocument(string Kind, string Name, System.Text.Json.JsonElement Root);

internal sealed record BrowserCase(string TestId, string Status, string? Priority);

internal sealed record RedactionFinding(string File, int Line, string Rule);

internal sealed record CampaignModel(
    CampaignManifest Manifest,
    IReadOnlyList<TestProjectSummary> TestProjects,
    CoverageSummary? Coverage,
    IReadOnlyList<GeneratorSummary> Generators,
    IReadOnlyList<KnownDefectResult> KnownDefects,
    IReadOnlyList<BenchmarkResult> Benchmarks,
    IReadOnlyList<ArtifactDocument> Artifacts,
    IReadOnlyList<BrowserCase> BrowserCases,
    IReadOnlyList<RedactionFinding> RawRedactionFindings);
