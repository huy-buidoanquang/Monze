using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monze.Hosting;
using Monze.Infrastructure.Persistence;
using Npgsql;

namespace Monze;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var builder = MonzeHostComposition.CreateBuilder(args);
        if (MonzeHostComposition.IsMigrateOnly(args))
        {
            using var migrationHost = builder.Build();
            await PostgresMigrator.ApplyAsync(
                migrationHost.Services.GetRequiredService<NpgsqlDataSource>(),
                migrationHost.Services.GetRequiredService<PostgresConnection>().Value,
                CancellationToken.None);
            return;
        }

        var host = builder.Build();
        await host.RunAsync();
    }
}
