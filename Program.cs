using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Hosting;
using Monze.Infrastructure.Caching;
using Monze.Infrastructure.Persistence;
using Npgsql;

namespace Monze;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = false;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";
            options.IncludeScopes = true;
        });
        var migrateOnly = args.Any(arg => arg.Equals("migrate", StringComparison.OrdinalIgnoreCase));
        var env = builder.Environment.EnvironmentName;
        builder.Configuration
            .AddJsonFile($"appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{env}.local.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.secrets.json", optional: true, reloadOnChange: false);
        var commandPrefix = builder.Configuration["Monze:Commands:Prefix"] ?? "*";
        var configuredRoot = builder.Configuration["Monze:Commands:Root"];
        var commandRoot = configuredRoot is null
            ? MonzeCommandNames.Monze
            : string.IsNullOrWhiteSpace(configuredRoot)
                ? null
                : configuredRoot.Trim();
        builder.Services.AddSingleton(new MonzeCommandOptions(commandPrefix, commandRoot));
        builder.Services.AddSingleton(MonzeConnectionRetryOptions.From(builder.Configuration));
        builder.Services.AddSingleton(new AiExecutionOptions(
            builder.Configuration.GetValue("Monze:Ai:DailyTokenCap", 2_000),
            builder.Configuration.GetValue("Monze:Ai:MaxInputCharacters", 8_000),
            builder.Configuration.GetValue("Monze:Ai:MaxConcurrentRequests", 2)).Normalize());
        builder.Services.AddSingleton(new MonzeCommandRateLimiter(new MonzeRateLimitOptions(
            builder.Configuration.GetValue("Monze:RateLimit:UserLimit", 8),
            TimeSpan.FromSeconds(Math.Clamp(builder.Configuration.GetValue("Monze:RateLimit:UserWindowSeconds", 10), 1, 3600)),
            builder.Configuration.GetValue("Monze:RateLimit:AiLimit", 3),
            TimeSpan.FromSeconds(Math.Clamp(builder.Configuration.GetValue("Monze:RateLimit:AiWindowSeconds", 60), 1, 3600)),
            builder.Configuration.GetValue("Monze:RateLimit:MeetingLimit", 2),
            TimeSpan.FromSeconds(Math.Clamp(builder.Configuration.GetValue("Monze:RateLimit:MeetingWindowSeconds", 10), 1, 3600)),
            builder.Configuration.GetValue("Monze:RateLimit:AdminLimit", 5),
            TimeSpan.FromSeconds(Math.Clamp(builder.Configuration.GetValue("Monze:RateLimit:AdminWindowSeconds", 60), 1, 3600)),
            Math.Clamp(builder.Configuration.GetValue("Monze:RateLimit:MaxEntries", 100_000), 1024, 1_000_000))));
        builder.Services.AddSingleton<IReadModelCache, DisabledReadModelCache>();
        builder.Services.AddSingleton<IWelcomeDraftStore, MemoryWelcomeDraftStore>();
        builder.Services.AddSingleton<IWelcomeSetupDraftStore, MemoryWelcomeSetupDraftStore>();
        var redis = builder.Configuration["Monze:Redis"];
        var botId = builder.Configuration.GetValue<long>("Mezon:BotId");
        if (!migrateOnly && !string.IsNullOrWhiteSpace(redis))
        {
            try
            {
                builder.Services.AddMonzeRedis(
                    redis,
                    builder.Configuration["Monze:EnvironmentName"] ?? env,
                    botId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Redis degraded; using PostgreSQL source of truth. Cause={ex.GetType().Name}");
            }
        }

        var postgres = builder.Configuration["Monze:Postgres"];
        if (string.IsNullOrWhiteSpace(postgres))
        {
            throw new InvalidOperationException("Set Monze:Postgres before starting Monze.");
        }

        var postgresBuilder = new NpgsqlConnectionStringBuilder(postgres)
        {
            MaxPoolSize = 64,
            MinPoolSize = 4,
            Timeout = 5,
            CommandTimeout = 15
        };
        var dataSource = NpgsqlDataSource.Create(postgresBuilder.ConnectionString);
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton(new PostgresConnection(postgresBuilder.ConnectionString));
        builder.Services.AddSingleton<IClanRegistryRepository, PostgresClanRegistryRepository>();
        builder.Services.AddSingleton<IAuthorizationRepository, PostgresAuthorizationRepository>();
        builder.Services.AddSingleton<IMeetingRepository, PostgresMeetingRepository>();
        builder.Services.AddSingleton<IScheduledMeetingRepository, PostgresScheduledMeetingRepository>();
        builder.Services.AddSingleton<ISchedulingRepository, PostgresSchedulingRepository>();
        builder.Services.AddSingleton<IOutboxRepository, PostgresOutboxRepository>();
        builder.Services.AddSingleton<IAiUsageRepository, PostgresAiUsageRepository>();
        builder.Services.AddSingleton<IMessageHistoryRepository, PostgresMessageHistoryRepository>();
        builder.Services.AddSingleton<ICommandInboxRepository, PostgresCommandInboxRepository>();
        builder.Services.AddSingleton<IUserProfileRepository, PostgresUserProfileRepository>();
        builder.Services.AddSingleton(sp => new MonzeApp(
            sp.GetRequiredService<IAuthorizationRepository>(),
            sp.GetRequiredService<IMeetingRepository>(),
            sp.GetRequiredService<ISchedulingRepository>(),
            sp.GetRequiredService<IAiUsageRepository>(),
            sp.GetRequiredService<IMessageHistoryRepository>(),
            sp.GetRequiredService<IWelcomeDraftStore>(),
            sp.GetService<IAiProvider>(),
            sp.GetRequiredService<IReadModelCache>(),
            sp.GetRequiredService<MonzeCommandOptions>(),
            sp.GetRequiredService<AiExecutionOptions>()));
        builder.Services.AddMemoryCache(options => options.SizeLimit = 64 * 1024 * 1024);
        var aiBase = builder.Configuration["Monze:Ai:BaseUrl"];
        var aiKey = builder.Configuration["Monze:Ai:ApiKey"];
        if (!string.IsNullOrWhiteSpace(aiBase) && !string.IsNullOrWhiteSpace(aiKey))
        {
            builder.Services.AddSingleton<HttpClient>(_ =>
            {
                var http = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Monze/1.0");
                return http;
            });
            builder.Services.AddSingleton<IAiProvider>(sp =>
                new OpenAiCompatibleProvider(
                    sp.GetRequiredService<HttpClient>(),
                    aiBase,
                    aiKey,
                    builder.Configuration["Monze:Ai:Model"] ?? "gpt-4o-mini",
                    sp.GetRequiredService<ILogger<OpenAiCompatibleProvider>>()));
        }

        if (migrateOnly)
        {
            using var migrationHost = builder.Build();
            await PostgresMigrator.ApplyAsync(
                migrationHost.Services.GetRequiredService<NpgsqlDataSource>(),
                migrationHost.Services.GetRequiredService<PostgresConnection>().Value,
                CancellationToken.None);
            return;
        }

        builder.Services.AddSingleton<ITranscriptClient>(sp =>
        {
            var baseUrl = sp.GetRequiredService<IConfiguration>()["Mezon:AgentBaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return new DisabledTranscriptClient();
            }

            var http = new HttpClient
            {
                BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(30)
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Monze/1.0");
            return new HttpTranscriptClient(http);
        });

        builder.Services.AddHostedService<StartupSchemaValidator>();
        builder.Services.AddHostedService<MonzeBot>();
        builder.Services.AddHostedService<MeetingMaintenanceWorker>();
        var host = builder.Build();
        await host.RunAsync();
    }
}
