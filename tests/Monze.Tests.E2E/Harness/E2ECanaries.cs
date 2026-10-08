using Npgsql;

namespace Monze.Tests.E2E.Harness;

/// <summary>
/// Secret canaries: unique fake credentials the area scenarios start Monze
/// with, so the oracles can prove none of them reaches a message or a log
/// line. Mezon:Token is the simulated bot token (it must match for login);
/// Monze:Ai:ApiKey is set without Monze:Ai:BaseUrl, which keeps the AI
/// provider unconfigured and therefore off the network. The agent and
/// transcript client use the same Mezon token. The campaign database
/// password (part of Monze:Postgres) is checked as a real secret as well.
/// </summary>
internal static class E2ECanaries
{
    public static readonly string MezonToken = $"canary-mezon-token-{Guid.NewGuid():N}";

    public static readonly string AiApiKey = $"canary-ai-key-{Guid.NewGuid():N}";

    /// <summary>Configuration entries that put the canaries into the host.</summary>
    public static IReadOnlyDictionary<string, string?> Configuration { get; } = new Dictionary<string, string?>
    {
        ["Monze:Ai:ApiKey"] = AiApiKey
    };

    /// <summary>Name and value of every secret the host was given.</summary>
    public static IReadOnlyList<(string Name, string Value)> Secrets(MonzeE2EHost host)
    {
        var secrets = new List<(string, string)>
        {
            ("Mezon:Token", host.World.Bot.Token),
            ("Monze:Ai:ApiKey", AiApiKey)
        };
        var password = new NpgsqlConnectionStringBuilder(host.Database.ConnectionString).Password;
        if (!string.IsNullOrEmpty(password))
        {
            secrets.Add(("Monze:Postgres password", password));
        }

        return secrets;
    }

    /// <summary>Replaces every secret value in <paramref name="text"/> with its name.</summary>
    public static string Redact(MonzeE2EHost host, string text)
    {
        foreach (var (name, value) in Secrets(host))
        {
            text = text.Replace(value, $"<{name}>", StringComparison.Ordinal);
        }

        return text;
    }
}
