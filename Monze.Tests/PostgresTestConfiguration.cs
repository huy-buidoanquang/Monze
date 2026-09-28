using System.Text.Json;

namespace Monze.Tests;

internal static class PostgresTestConfiguration
{
    public static string? ReadConnectionString()
    {
        var configPath = FindConfigPath();
        if (configPath is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement;
        if (root.TryGetProperty("Monze", out var monze)
            && monze.TryGetProperty("Postgres", out var postgres)
            && postgres.ValueKind == JsonValueKind.String)
        {
            var configured = postgres.GetString();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }
        }

        var secretsPath = Path.Combine(Path.GetDirectoryName(configPath)!, "appsettings.secrets.json");
        if (!File.Exists(secretsPath))
        {
            return null;
        }

        using var secrets = JsonDocument.Parse(File.ReadAllText(secretsPath));
        return secrets.RootElement.TryGetProperty("Monze", out var secretMonze)
            && secretMonze.TryGetProperty("Postgres", out var secretPostgres)
            && secretPostgres.ValueKind == JsonValueKind.String
            ? secretPostgres.GetString()
            : null;
    }

    private static string? FindConfigPath()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        for (var directory = current; directory is not null; directory = directory.Parent)
        {
            var nested = Path.Combine(directory.FullName, "Monze", "appsettings.json");
            if (File.Exists(nested))
            {
                return nested;
            }

            var direct = Path.Combine(directory.FullName, "appsettings.json");
            if (File.Exists(direct))
            {
                return direct;
            }
        }

        return null;
    }
}
