using Microsoft.Extensions.Hosting;
using Npgsql;
using Monze.Infrastructure.Persistence;

namespace Monze.Hosting;

internal sealed class StartupSchemaValidator : IHostedService
{
    public static Task Ready { get; private set; } = Task.CompletedTask;

    private readonly NpgsqlDataSource _dataSource;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartupSchemaValidator(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
        Ready = _ready.Task;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PostgresMigrator.ValidateAsync(_dataSource, cancellationToken);
            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
