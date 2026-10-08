using System.Text;
using System.Text.Json;

namespace Monze.Testing;

/// <summary>
/// Expected-failure support for defects that are known but not yet fixed.
/// The callback asserts the CORRECT behaviour. While the defect exists the
/// assertions fail, which is recorded as "xfail" and the test passes. Once the
/// defect is fixed the assertions pass, which throws <see cref="XPassException"/>
/// so the marker has to be removed together with the fix.
/// </summary>
public static class KnownDefect
{
    private static readonly object Gate = new();

    public static async Task ExpectFailureAsync(string defectId, Func<Task> correctBehaviour)
    {
        try
        {
            await correctBehaviour();
        }
        catch (Exception ex) when (ex is not XPassException)
        {
            Write(defectId, "xfail", ex.GetType().Name);
            return;
        }

        Write(defectId, "xpass", null);
        throw new XPassException(defectId);
    }

    public static void ExpectFailure(string defectId, Action correctBehaviour)
    {
        try
        {
            correctBehaviour();
        }
        catch (Exception ex) when (ex is not XPassException)
        {
            Write(defectId, "xfail", ex.GetType().Name);
            return;
        }

        Write(defectId, "xpass", null);
        throw new XPassException(defectId);
    }

    private static void Write(string defectId, string outcome, string? exceptionType)
    {
        var directory = CampaignEnvironment.LedgerDirectory;
        if (directory is null)
        {
            return;
        }

        var line = JsonSerializer.Serialize(new
        {
            schema = "monze.defect.v1",
            campaign = CampaignEnvironment.Id,
            defectId,
            outcome,
            exceptionType,
            utc = DateTimeOffset.UtcNow
        });
        lock (Gate)
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "known-defects.jsonl"), line + Environment.NewLine, new UTF8Encoding(false));
        }
    }
}

public sealed class XPassException : Exception
{
    public XPassException(string defectId)
        : base($"{defectId} no longer reproduces. Remove the KnownDefect marker in the same change as the fix.")
    {
        DefectId = defectId;
    }

    public string DefectId { get; }
}
