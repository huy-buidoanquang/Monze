using Monze.Application.Commands;

namespace Monze;

/// <summary>
/// Bounded tag values for monze.command.duration. User-typed command text
/// never becomes a tag: unrecognised commands are reported as "unknown".
/// </summary>
internal static class MonzeCommandMetricTags
{
    public const string Completed = "completed";
    public const string Duplicate = "duplicate";
    public const string ClaimFailed = "claim_failed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Uncertain = "uncertain";

    public static string Module(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return MonzeCommandNames.Unknown;
        }

        var normalized = MonzeCommandNames.Normalize(command);
        return normalized switch
        {
            MonzeCommandNames.Monze
                or MonzeCommandNames.Meeting
                or MonzeCommandNames.Summary
                or MonzeCommandNames.Help
                or MonzeCommandNames.Setup
                or MonzeCommandNames.Welcome
                or MonzeCommandNames.Ai
                or MonzeCommandNames.Translate
                or MonzeCommandNames.Composer
                or MonzeCommandNames.Simplify
                or MonzeCommandNames.Role
                or MonzeCommandNames.Avatar => normalized,
            _ => MonzeCommandNames.Unknown
        };
    }
}
