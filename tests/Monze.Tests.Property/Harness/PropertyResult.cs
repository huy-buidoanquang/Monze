namespace Monze.Tests.Property.Harness;

/// <summary>
/// Outcome of one generated check. <see cref="Input"/> is a short, redacted
/// description; <see cref="Tags"/> carry the declared dimensions for pairwise
/// coverage. A check inside a known-defect class reports
/// <see cref="DefectId"/>: "xfail" while the defect reproduces, "pass" once
/// the code behaves correctly for that input.
/// </summary>
internal sealed record PropertyResult(
    string Outcome,
    string Input,
    IReadOnlyDictionary<string, string> Tags,
    string? Note = null,
    string? DefectId = null)
{
    public bool IsFailure => Outcome == "fail";

    public static PropertyResult Pass(string input, IReadOnlyDictionary<string, string> tags, string? defectClass = null)
        => new("pass", input, tags, null, defectClass);

    public static PropertyResult Fail(string input, IReadOnlyDictionary<string, string> tags, string note)
        => new("fail", input, tags, note);

    public static PropertyResult Known(string defectId, string input, IReadOnlyDictionary<string, string> tags, string note)
        => new("xfail", input, tags, note, defectId);

    /// <summary>Pass when <paramref name="ok"/>, otherwise a failure with <paramref name="note"/>.</summary>
    public static PropertyResult Check(bool ok, string input, IReadOnlyDictionary<string, string> tags, Func<string> note)
        => ok ? Pass(input, tags) : Fail(input, tags, note());
}
