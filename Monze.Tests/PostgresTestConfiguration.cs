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

        for (var directory = new DirectoryInfo(Path.GetDirectoryName(configPath)!);
            directory is not null;
            directory = directory.Parent)
        {
            var candidates = new[]
            {
                Path.Combine(directory.FullName, "appsettings.Development.local.json"),
                Path.Combine(directory.FullName, "appsettings.secrets.json")
            };
            foreach (var secretsPath in candidates)
            {
                if (!File.Exists(secretsPath))
                {
                    continue;
                }

                using var secrets = JsonDocument.Parse(File.ReadAllText(secretsPath));
                if (secrets.RootElement.TryGetProperty("Monze", out var secretMonze)
                    && secretMonze.TryGetProperty("Postgres", out var secretPostgres)
                    && secretPostgres.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(secretPostgres.GetString()))
                {
                    return secretPostgres.GetString();
                }
            }
        }

        return null;
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
