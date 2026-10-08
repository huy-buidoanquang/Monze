using System.Diagnostics;
using Monze.Infrastructure.Persistence;
using Monze.Testing;
using Npgsql;
using Xunit;

namespace Monze.Tests;

/// <summary>
/// Runs the real "Monze migrate" entry point as a child process. The working
/// directory and content root are an empty temp directory, so no appsettings
/// file can be found, and inherited Monze/Mezon variables are removed: the
/// only database the process can reach is the guarded campaign database.
/// </summary>
public sealed class MigrateCommandProcessTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    [DbFact]
    [Req("REQ-MIG-001", "REQ-SEC-101")]
    public async Task Migrate_command_creates_and_migrates_an_empty_database_and_is_repeatable()
    {
        var campaign = new NpgsqlConnectionStringBuilder(TestPostgres.ConnectionString);
        var target = new NpgsqlConnectionStringBuilder(campaign.ConnectionString)
        {
            Database = $"monze_t_migrate_{Guid.NewGuid():N}"
        };
        TestPostgres.AssertCampaignDatabase(target.ConnectionString);
        var monze = Path.Combine(RepositoryPaths.Root, "bin", RepositoryPaths.Configuration, "net10.0", "Monze.dll");
        Assert.True(File.Exists(monze), $"Build Monze.csproj ({RepositoryPaths.Configuration}) before running this test.");
        var workDirectory = Directory.CreateTempSubdirectory("monze-migrate-").FullName;
        try
        {
            await RunMigrateAsync(monze, workDirectory, target.ConnectionString);
            await using (var dataSource = NpgsqlDataSource.Create(target.ConnectionString))
            {
                await PostgresMigrator.ValidateAsync(dataSource, CancellationToken.None);
            }

            await RunMigrateAsync(monze, workDirectory, target.ConnectionString);
            await using (var dataSource = NpgsqlDataSource.Create(target.ConnectionString))
            {
                await PostgresMigrator.ValidateAsync(dataSource, CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(workDirectory, recursive: true);
            await DropDatabaseAsync(campaign, target.Database!);
        }
    }

    private static async Task RunMigrateAsync(string monze, string workDirectory, string connectionString)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(monze);
        start.ArgumentList.Add("--contentRoot");
        start.ArgumentList.Add(workDirectory);
        start.ArgumentList.Add("migrate");
        foreach (var key in start.Environment.Keys
                     .Where(key => key.StartsWith("Monze__", StringComparison.OrdinalIgnoreCase)
                                   || key.StartsWith("Mezon__", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            start.Environment.Remove(key);
        }

        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Monze__Postgres"] = connectionString;
        start.Environment["Monze__Logging__Directory"] = Path.Combine(workDirectory, "logs");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("dotnet could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Monze migrate did not exit within {ProcessTimeout.TotalMinutes} minutes.");
        }

        var output = await standardOutput + await standardError;
        Assert.True(process.ExitCode == 0, $"Monze migrate exited with {process.ExitCode}:{Environment.NewLine}{Tail(output)}");
    }

    private static async Task DropDatabaseAsync(NpgsqlConnectionStringBuilder campaign, string database)
    {
        Assert.StartsWith("monze_t_migrate_", database, StringComparison.Ordinal);
        NpgsqlConnection.ClearAllPools();
        var admin = new NpgsqlConnectionStringBuilder(campaign.ConnectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS {PostgresMigrator.QuoteIdentifier(database)} WITH (FORCE);",
            connection);
        await drop.ExecuteNonQueryAsync();
    }

    private static string Tail(string output)
    {
        var lines = output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.TakeLast(20));
    }
}
