using System.Text;
using System.Text.Json;

namespace Monze.Testing;

/// <summary>
/// Records generated test cases as JSONL (schema monze.case.v1) so a property
/// test can run hundreds of thousands of checks inside one xUnit test while
/// the campaign report still sees every outcome, tag and pairwise dimension.
/// Detailed lines are capped per test; the closing summary line always holds
/// the full counts.
/// </summary>
public sealed class CaseLedger : IDisposable
{
    public const string Schema = "monze.case.v1";
    private const int DetailCap = 10_000;
    private const int MissingPairCap = 50;

    private readonly object _gate = new();
    private readonly string _suite;
    private readonly string _testId;
    private readonly StreamWriter? _writer;
    private readonly Dictionary<string, long> _outcomes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _tags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _dimensions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _infeasiblePairs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observedPairs = new(StringComparer.Ordinal);
    private readonly HashSet<ulong> _inputs = new();
    private readonly List<string> _failures = new();
    private long _detailed;
    private bool _disposed;

    private CaseLedger(string suite, string testId, StreamWriter? writer)
    {
        _suite = suite;
        _testId = testId;
        _writer = writer;
    }

    public long Total
    {
        get
        {
            lock (_gate)
            {
                return _outcomes.Values.Sum();
            }
        }
    }

    /// <summary>
    /// Checks the test intended to run. When the summary total is lower, the
    /// run was cut by its time cap and the report shows it as under-sampled.
    /// </summary>
    public long? Requested { get; set; }

    public IReadOnlyList<string> Failures
    {
        get
        {
            lock (_gate)
            {
                return _failures.ToArray();
            }
        }
    }

    public static CaseLedger Open(string suite, string testId)
    {
        var directory = CampaignEnvironment.LedgerDirectory;
        StreamWriter? writer = null;
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{Sanitize(suite)}.{Sanitize(testId)}.jsonl");
            writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        }

        return new CaseLedger(suite, testId, writer);
    }

    /// <summary>A ledger that counts but never writes, for tests of the test harness itself.</summary>
    public static CaseLedger Unrecorded(string suite, string testId) => new(suite, testId, null);

    /// <summary>Declares a finite dimension so the summary can report pairwise coverage.</summary>
    public CaseLedger Dimension(string name, params string[] values)
    {
        lock (_gate)
        {
            _dimensions[name] = values.Distinct(StringComparer.Ordinal).ToArray();
        }

        return this;
    }

    /// <summary>Marks a pair of dimension values that cannot occur together.</summary>
    public CaseLedger Infeasible(string firstDimension, string firstValue, string secondDimension, string secondValue)
    {
        lock (_gate)
        {
            _infeasiblePairs.Add(PairKey(firstDimension, firstValue, secondDimension, secondValue));
        }

        return this;
    }

    public long Count(string outcome)
    {
        lock (_gate)
        {
            return _outcomes.TryGetValue(outcome, out var count) ? count : 0;
        }
    }

    /// <summary>
    /// Records one check. <paramref name="input"/> must already be a redacted,
    /// short description; it is digested for distinct-input counting and only
    /// written verbatim for failures and the first detailed lines.
    /// </summary>
    public void Record(
        string outcome,
        string input,
        IReadOnlyDictionary<string, string>? tags = null,
        string? seed = null,
        string? note = null)
    {
        lock (_gate)
        {
            _outcomes[outcome] = (_outcomes.TryGetValue(outcome, out var count) ? count : 0) + 1;
            _inputs.Add(Digest(input));
            if (tags is not null)
            {
                foreach (var tag in tags)
                {
                    var key = $"{tag.Key}={tag.Value}";
                    _tags[key] = (_tags.TryGetValue(key, out var tagCount) ? tagCount : 0) + 1;
                }

                var ordered = tags.OrderBy(static tag => tag.Key, StringComparer.Ordinal).ToArray();
                for (var i = 0; i < ordered.Length; i++)
                {
                    for (var j = i + 1; j < ordered.Length; j++)
                    {
                        _observedPairs.Add(PairKey(ordered[i].Key, ordered[i].Value, ordered[j].Key, ordered[j].Value));
                    }
                }
            }

            var failed = outcome is "fail" or "xpass";
            if (failed && _failures.Count < 100)
            {
                _failures.Add(note is null ? input : $"{input} :: {note}");
            }

            if (_writer is not null && (_detailed < DetailCap || failed))
            {
                _detailed++;
                _writer.WriteLine(JsonSerializer.Serialize(new
                {
                    schema = Schema,
                    kind = "case",
                    campaign = CampaignEnvironment.Id,
                    suite = _suite,
                    testId = _testId,
                    outcome,
                    inputDigest = Digest(input).ToString("x16"),
                    input = failed || _detailed <= 200 ? input : null,
                    seed,
                    note,
                    tags
                }));
            }
        }
    }

    public void Pass(string input, IReadOnlyDictionary<string, string>? tags = null)
        => Record("pass", input, tags);

    public void Fail(string input, string note, IReadOnlyDictionary<string, string>? tags = null, string? seed = null)
        => Record("fail", input, tags, seed, note);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_writer is null)
            {
                return;
            }

            var (required, covered, missing) = PairwiseCoverage();
            _writer.WriteLine(JsonSerializer.Serialize(new
            {
                schema = Schema,
                kind = "summary",
                campaign = CampaignEnvironment.Id,
                suite = _suite,
                testId = _testId,
                total = _outcomes.Values.Sum(),
                requested = Requested,
                distinctInputs = _inputs.Count,
                outcomes = _outcomes,
                tags = _tags,
                pairwise = new { required, covered, missing },
                failures = _failures
            }));
            _writer.Dispose();
        }
    }

    /// <summary>Pairwise coverage over the declared dimensions (for assertions inside tests).</summary>
    public (int Required, int Covered, IReadOnlyList<string> Missing) PairwiseCoverage()
    {
        lock (_gate)
        {
            var names = _dimensions.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
            var required = 0;
            var covered = 0;
            var missing = new List<string>();
            for (var i = 0; i < names.Length; i++)
            {
                for (var j = i + 1; j < names.Length; j++)
                {
                    foreach (var first in _dimensions[names[i]])
                    {
                        foreach (var second in _dimensions[names[j]])
                        {
                            var key = PairKey(names[i], first, names[j], second);
                            if (_infeasiblePairs.Contains(key))
                            {
                                continue;
                            }

                            required++;
                            if (_observedPairs.Contains(key))
                            {
                                covered++;
                            }
                            else if (missing.Count < MissingPairCap)
                            {
                                missing.Add(key);
                            }
                        }
                    }
                }
            }

            return (required, covered, missing);
        }
    }

    private static string PairKey(string firstDimension, string firstValue, string secondDimension, string secondValue)
        => string.CompareOrdinal(firstDimension, secondDimension) <= 0
            ? $"{firstDimension}={firstValue}|{secondDimension}={secondValue}"
            : $"{secondDimension}={secondValue}|{firstDimension}={firstValue}";

    private static ulong Digest(string value)
    {
        var hash = 14695981039346656037UL;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        return builder.ToString();
    }
}
