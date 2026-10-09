using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Monze.Infrastructure.Persistence;

namespace Monze.Hosting;

/// <summary>
/// Checks the schema before the workers start. PostgreSQL that is not
/// reachable yet (started together with the bot, a slow network) is retried
/// with a growing delay for up to five minutes; a schema that is missing or
/// does not match, or a refused login, stops the host at once.
/// </summary>
internal sealed class StartupSchemaValidator : IHostedService
{
    private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly NpgsqlDataSource _dataSource;
    private readonly StartupReadiness _readiness;
    private readonly TimeProvider _time;
    private readonly ILogger<StartupSchemaValidator> _logger;

    public StartupSchemaValidator(
        NpgsqlDataSource dataSource,
        StartupReadiness readiness,
        TimeProvider time,
        ILogger<StartupSchemaValidator> logger)
    {
        _dataSource = dataSource;
        _readiness = readiness;
        _time = time;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        var delay = TimeSpan.FromSeconds(1);
        try
        {
            while (true)
            {
                try
                {
                    await PostgresMigrator.ValidateAsync(_dataSource, cancellationToken);
                    break;
                }
                catch (NpgsqlException ex) when (ex.IsTransient && _time.GetElapsedTime(started) + delay <= RetryWindow)
                {
                    _logger.LogWarning(ex, "PostgreSQL is not reachable for the schema check; retrying in {Delay}.", delay);
                }

                await Task.Delay(delay, _time, cancellationToken);
                delay = delay * 2 < MaxRetryDelay ? delay * 2 : MaxRetryDelay;
            }

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
