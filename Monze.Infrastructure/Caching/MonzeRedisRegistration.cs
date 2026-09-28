using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Monze.Infrastructure.Caching;

public static class MonzeRedisRegistration
{
    public static void AddMonzeRedis(
        this IServiceCollection services,
        string connectionString,
        string environment,
        long botId)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 3;
        options.ConnectTimeout = 1_000;
        options.AsyncTimeout = 1_000;
        options.SyncTimeout = 1_000;
        options.KeepAlive = 30;
        options.ReconnectRetryPolicy = new ExponentialRetry(5_000);
        var multiplexer = ConnectionMultiplexer.Connect(options);
        services.AddSingleton<IConnectionMultiplexer>(multiplexer);
        services.AddSingleton<MonzeReadModelCache>(provider =>
            new MonzeReadModelCache(
                multiplexer,
                environment,
                botId,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MonzeReadModelCache>>()));
        services.Replace(
            ServiceDescriptor.Singleton<Monze.Application.IReadModelCache>(
                provider => provider.GetRequiredService<MonzeReadModelCache>()));
    }
}
