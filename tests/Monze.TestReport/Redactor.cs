using System.Text.Json;
using System.Text.RegularExpressions;

namespace Monze.TestReport;

/// <summary>
/// Keeps secrets and raw platform ids out of the report. Literal secret values
/// are loaded from the local settings files and the campaign secrets file and
/// only ever held in memory. <see cref="Scrub"/> cleans free text before it is
/// rendered; <see cref="Scan"/> is the final gate over the written report.
/// </summary>
internal sealed partial class Redactor
{
    private const int MinimumSecretLength = 8;
    private static readonly string[] SettingsFiles =
    [
        "appsettings.json",
        "appsettings.secrets.json",
        "appsettings.Development.local.json"
    ];

    private readonly string[] _secrets;

    public Redactor(IEnumerable<string> secrets)
    {
        _secrets = secrets
            .Where(static value => !string.IsNullOrWhiteSpace(value) && value.Length >= MinimumSecretLength)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static value => value.Length)
            .ToArray();
    }

    public int SecretCount => _secrets.Length;

    public static Redactor FromSources(string? repositoryRoot, string? campaignSecretsFile)
    {
        var secrets = new List<string>();
        if (repositoryRoot is not null)
        {
            foreach (var name in SettingsFiles)
            {
                var path = Path.Combine(repositoryRoot, name);
                if (File.Exists(path))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(path));
                        CollectSecrets(document.RootElement, string.Empty, secrets);
                    }
                    catch (JsonException)
                    {
                        // A malformed settings file has no values we can match; the
                        // pattern rules still apply.
                    }
                }
            }
        }

        if (campaignSecretsFile is not null && File.Exists(campaignSecretsFile))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(campaignSecretsFile));
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
                    {
                        secrets.Add(value);
                    }
                }
            }
        }

        return new Redactor(secrets);
    }

    /// <summary>Replaces secrets, credential patterns and long numeric ids.</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        foreach (var secret in _secrets)
        {
            result = result.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        result = DsnPassword().Replace(result, "$1=[redacted]");
        result = CredentialUrl().Replace(result, "$1://[redacted]@");
        result = Bearer().Replace(result, "Bearer [redacted]");
        result = Jwt().Replace(result, "[redacted-jwt]");
        result = ApiKey().Replace(result, "[redacted-key]");
        result = Sentinel().Replace(result, "[sentinel]");
        result = LongId().Replace(result, "[id]");
        return result;
    }

    /// <summary>Reports every rule violation in <paramref name="text"/>; values are never returned.</summary>
    public IReadOnlyList<RedactionFinding> Scan(string file, string text)
    {
        var findings = new List<RedactionFinding>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (var secret in _secrets)
            {
                if (line.Contains(secret, StringComparison.Ordinal))
                {
                    findings.Add(new RedactionFinding(file, i + 1, "secret-value"));
                    break;
                }
            }

            Add(findings, file, i + 1, line, DsnPassword(), "dsn-credential");
            Add(findings, file, i + 1, line, CredentialUrl(), "credential-url");
            Add(findings, file, i + 1, line, Bearer(), "bearer-token");
            Add(findings, file, i + 1, line, Jwt(), "jwt");
            Add(findings, file, i + 1, line, ApiKey(), "api-key");
            Add(findings, file, i + 1, line, Sentinel(), "sentinel");
            Add(findings, file, i + 1, line, LongId(), "long-id");
        }

        return findings;
    }

    private static void Add(List<RedactionFinding> findings, string file, int line, string text, Regex rule, string name)
    {
        if (rule.IsMatch(text))
        {
            findings.Add(new RedactionFinding(file, line, name));
        }
    }

    private static void CollectSecrets(JsonElement element, string path, List<string> secrets)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectSecrets(property.Value, path + ":" + property.Name, secrets);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectSecrets(item, path, secrets);
                }

                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (value is not null && SecretKeyName().IsMatch(path))
                {
                    secrets.Add(value);
                    var password = DsnPassword().Match(value);
                    if (password.Success)
                    {
                        secrets.Add(password.Groups[2].Value);
                    }
                }

                break;
        }
    }

    [GeneratedRegex(@"(?i)(token|password|secret|apikey|api_key|:key$|postgres|redis|connection|dsn)")]
    private static partial Regex SecretKeyName();

    // A value that starts with '[' is an already redacted placeholder.
    [GeneratedRegex(@"(?i)\b(password|pwd)\s*=\s*([^;\s""'\[][^;\s""']*)")]
    private static partial Regex DsnPassword();

    [GeneratedRegex(@"(?i)\b(postgres(?:ql)?|redis|amqp|mongodb|https?)://[^\s/:@]+:[^\s/@]+@")]
    private static partial Regex CredentialUrl();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9\-._~+/]{16,}=*")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{16,}")]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"CAMPAIGN-(?:PROMPT|TRANSCRIPT)-[A-Za-z0-9-]*|CANARY-[A-Za-z0-9-]*")]
    private static partial Regex Sentinel();

    [GeneratedRegex(@"(?<![\w.:/-])-?\d{15,}(?![\w.])")]
    private static partial Regex LongId();
}
