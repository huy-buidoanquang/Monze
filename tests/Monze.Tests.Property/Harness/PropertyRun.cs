using System.Diagnostics;
using CsCheck;
using Monze.Testing;
using Xunit.Sdk;

namespace Monze.Tests.Property.Harness;

/// <summary>
/// Runs one generator as one xUnit test. Cases are generated from a
/// per-generator PCG stream seeded by RunSeed, so a campaign replays exactly;
/// every case is recorded in the case ledger. Seeds listed in
/// Regressions/&lt;id&gt;.seeds run first. On failure the first failing case is
/// shrunk with CsCheck and the test fails with the original input, the shrunk
/// input and the seed that reproduces it.
/// </summary>
internal static class PropertyRun
{
    private static readonly TimeSpan DefaultTimeCap = TimeSpan.FromMinutes(3);

    public static void Run<T>(
        string generatorId,
        Gen<T> gen,
        Func<T, PropertyResult> check,
        int iterations,
        Action<CaseLedger>? declare = null,
        IReadOnlyCollection<string>? knownDefects = null,
        Func<T, string>? print = null,
        TimeSpan? timeCap = null,
        bool recordToCampaign = true)
    {
        var requested = (long)iterations * CampaignEnvironment.PbtScale;
        var cap = (timeCap ?? DefaultTimeCap) * CampaignEnvironment.PbtScale;
        using var ledger = recordToCampaign
            ? CaseLedger.Open("property", generatorId)
            : CaseLedger.Unrecorded("property", generatorId);
        declare?.Invoke(ledger);
        ledger.Requested = requested;

        var stream = (uint)(RunSeed.For(generatorId) >> 33) | 1u;
        var master = new PCG(stream, RunSeed.For(generatorId));
        var defects = (knownDefects ?? []).ToDictionary(static id => id, static _ => (Xfail: 0L, Passed: 0L), StringComparer.Ordinal);
        string? firstFailureSeed = null;
        PropertyResult? firstFailure = null;
        var failures = 0L;

        void Execute(PCG casePcg)
        {
            var seedText = casePcg.ToString();
            var value = gen.Generate(casePcg, null, out _);
            PropertyResult result;
            try
            {
                result = check(value);
            }
            catch (Exception ex) when (ex is not XPassException)
            {
                result = PropertyResult.Fail(print?.Invoke(value) ?? Check.Print(value), new Dictionary<string, string>(), $"{ex.GetType().Name}: {ex.Message}");
            }

            ledger.Record(result.Outcome, result.Input, result.Tags, result.IsFailure ? seedText : null, result.Note);
            if (result.DefectId is { } defectId)
            {
                if (!defects.TryGetValue(defectId, out var counts))
                {
                    throw new InvalidOperationException($"{generatorId} reported undeclared defect class {defectId}.");
                }

                defects[defectId] = result.Outcome == "xfail" ? (counts.Xfail + 1, counts.Passed) : (counts.Xfail, counts.Passed + 1);
            }

            if (result.IsFailure)
            {
                failures++;
                if (firstFailure is null)
                {
                    firstFailure = result;
                    firstFailureSeed = seedText;
                }
            }
        }

        foreach (var regression in RegressionSeeds(generatorId))
        {
            Execute(PCG.Parse(regression));
        }

        var watch = Stopwatch.StartNew();
        for (var i = 0L; i < requested && watch.Elapsed < cap; i++)
        {
            Execute(new PCG(stream, master.Next64()));
        }

        if (firstFailure is not null)
        {
            throw new XunitException(
                $"{generatorId}: {failures:N0}/{ledger.Total:N0} checks failed.{Environment.NewLine}"
                + $"First: {firstFailure.Input} :: {firstFailure.Note}{Environment.NewLine}"
                + $"Seed: {firstFailureSeed} (add it to tests/Monze.Tests.Property/Regressions/{generatorId}.seeds){Environment.NewLine}"
                + Shrink(gen, check, firstFailureSeed!, print));
        }

        foreach (var (defectId, counts) in defects)
        {
            if (counts.Xfail == 0 && counts.Passed == 0)
            {
                throw new XunitException($"{generatorId} never generated an input in the class of {defectId}; fix the generator weights.");
            }

            KnownDefect.ExpectFailure(
                defectId,
                () =>
                {
                    if (counts.Xfail > 0)
                    {
                        throw new XunitException($"{defectId}: {counts.Xfail:N0} generated cases still show the defect.");
                    }
                },
                recordToCampaign);
        }

        var (required, covered, missing) = ledger.PairwiseCoverage();
        if (covered < required)
        {
            throw new XunitException(
                $"{generatorId}: pairwise coverage {covered}/{required}; missing {string.Join(", ", missing)}");
        }
    }

    private static string Shrink<T>(Gen<T> gen, Func<T, PropertyResult> check, string seed, Func<T, string>? print)
    {
        try
        {
            gen.Sample(
                value =>
                {
                    try
                    {
                        return !check(value).IsFailure;
                    }
                    catch (Exception ex) when (ex is not XPassException)
                    {
                        return false;
                    }
                },
                seed: seed,
                iter: 5_000,
                time: 5,
                threads: 1,
                print: print);
            return "Shrink: the failure did not reproduce from its seed.";
        }
        catch (Exception ex)
        {
            return $"Shrink: {ex.Message}";
        }
    }

    private static IEnumerable<string> RegressionSeeds(string generatorId)
    {
        var path = Path.Combine(RepositoryPaths.Root, "tests", "Monze.Tests.Property", "Regressions", $"{generatorId}.seeds");
        return File.Exists(path)
            ? File.ReadAllLines(path).Select(static line => line.Trim()).Where(static line => line.Length > 0 && !line.StartsWith('#'))
            : [];
    }
}
