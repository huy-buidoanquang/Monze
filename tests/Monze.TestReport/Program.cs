namespace Monze.TestReport;

internal static class Program
{
    private static int Main(string[] args)
    {
        string? campaign = null;
        string? repository = null;
        string? secrets = Environment.GetEnvironmentVariable("MONZE_CAMPAIGN_SECRETS_FILE");
        string? browser = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--campaign-dir":
                    campaign = args[++i];
                    break;
                case "--repo-root":
                    repository = args[++i];
                    break;
                case "--secrets-file":
                    secrets = args[++i];
                    break;
                case "--browser-ledger":
                    browser = args[++i];
                    break;
            }
        }

        if (campaign is null || !Directory.Exists(campaign))
        {
            Console.Error.WriteLine("Usage: Monze.TestReport --campaign-dir <dir> [--repo-root <dir>] [--secrets-file <path>] [--browser-ledger <path>]");
            return ReportBuilder.BuilderError;
        }

        try
        {
            var result = ReportBuilder.Build(campaign, repository, secrets, browser);
            Console.WriteLine(result.ExitCode == ReportBuilder.Success
                ? $"Report written: {result.ReportPath} (raw findings: {result.RawFindings})"
                : $"Redaction violation: {result.ReportFindings.Count} finding(s); safe stub written to {result.ReportPath}");
            return result.ExitCode;
        }
        catch (Exception ex)
        {
            // Never echo exception messages: they may carry paths or values.
            Console.Error.WriteLine($"Report builder failed: {ex.GetType().Name}");
            return ReportBuilder.BuilderError;
        }
    }
}
