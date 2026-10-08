namespace Monze.Testing;

/// <summary>
/// Environment switches shared by every test tier. The campaign orchestrator
/// (scripts/run-test-campaign.ps1) sets them; a developer running a single
/// test project gets safe defaults.
/// </summary>
public static class CampaignEnvironment
{
    public const string IdVariable = "MONZE_CAMPAIGN_ID";
    public const string StrictVariable = "MONZE_CAMPAIGN_STRICT";
    public const string LedgerDirectoryVariable = "MONZE_CASE_LEDGER_DIR";
    public const string PbtScaleVariable = "MONZE_PBT_SCALE";
    public const string SeedVariable = "MONZE_CAMPAIGN_SEED";

    public static string Id => Read(IdVariable) ?? "local";

    /// <summary>
    /// In strict mode a test that needs infrastructure fails instead of
    /// being skipped, so a campaign can never report a vacuous pass.
    /// </summary>
    public static bool Strict => Read(StrictVariable) == "1";

    public static string? LedgerDirectory => Read(LedgerDirectoryVariable);

    public static int PbtScale
    {
        get
        {
            var value = Read(PbtScaleVariable);
            return int.TryParse(value, out var scale) && scale > 0 ? Math.Min(scale, 1000) : 1;
        }
    }

    public static string? Seed => Read(SeedVariable);

    private static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
