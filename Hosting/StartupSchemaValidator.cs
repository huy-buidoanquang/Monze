using Microsoft.Extensions.Hosting;
using Npgsql;
using Monze.Infrastructure.Persistence;

namespace Monze.Hosting;

internal sealed class StartupSchemaValidator : IHostedService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly StartupReadiness _readiness;

    public StartupSchemaValidator(NpgsqlDataSource dataSource, StartupReadiness readiness)
    {
        _dataSource = dataSource;
        _readiness = readiness;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PostgresMigrator.ValidateAsync(_dataSource, cancellationToken);
            _readiness.MarkReady();
        }
        catch (Exception ex)
        {
            _readiness.MarkFailed(ex);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
