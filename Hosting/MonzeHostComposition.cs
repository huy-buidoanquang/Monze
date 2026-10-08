using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Hosting.Logging;
using Monze.Infrastructure.Caching;
using Monze.Infrastructure.Persistence;
using Npgsql;

namespace Monze.Hosting;

/// <summary>
/// Builds the Monze host. Program.Main uses the production options; tests use
/// the same composition with in-memory configuration and service overrides.
/// </summary>
internal static class MonzeHostComposition
{
    public static bool IsMigrateOnly(string[] args)
        => args.Any(arg => arg.Equals("migrate", StringComparison.OrdinalIgnoreCase));

    public static HostApplicationBuilder CreateBuilder(string[] args, MonzeHostCompositionOptions? options = null)
    {
        options ??= MonzeHostCompositionOptions.Production;
        var builder = options.Settings is null
            ? Host.CreateApplicationBuilder(args)
            : Host.CreateApplicationBuilder(options.Settings);
        if (options.Configuration is not null)
        {
            builder.Configuration.AddInMemoryCollection(options.Configuration);
        }

        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        if (options.AddProductionLogging)
        {
            builder.Logging.AddSimpleConsole(console =>
            {
                console.SingleLine = false;
                console.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";
                console.IncludeScopes = true;
            });
            builder.Logging.AddProvider(new DailyFileLoggerProvider(
                DailyFileLoggerOptions.From(builder.Configuration)));
        }

        var migrateOnly = IsMigrateOnly(args);
        var env = builder.Environment.EnvironmentName;
        if (options.LoadAppSettingsFiles)
        {
            builder.Configuration
                .AddJsonFile($"appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile($"appsettings.{env}.local.json", optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.secrets.json", optional: true, reloadOnChange: false);
        }

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
        builder.Services.AddSingleton<IWelcomeRepository, PostgresWelcomeRepository>();
        builder.Services.AddSingleton<IRoleRepository, PostgresRoleRepository>();
        builder.Services.AddSingleton<IMeetingRepository, PostgresMeetingRepository>();
        builder.Services.AddSingleton<IScheduledMeetingRepository, PostgresScheduledMeetingRepository>();
        builder.Services.AddSingleton<ISchedulingRepository, PostgresSchedulingRepository>();
        builder.Services.AddSingleton<IOutboxRepository, PostgresOutboxRepository>();
        builder.Services.AddSingleton<IAiUsageRepository, PostgresAiUsageRepository>();
        builder.Services.AddSingleton<IMessageHistoryRepository, PostgresMessageHistoryRepository>();
        builder.Services.AddSingleton<ICommandInboxRepository, PostgresCommandInboxRepository>();
        builder.Services.AddSingleton<IInteractionInboxRepository, PostgresInteractionInboxRepository>();
        builder.Services.AddSingleton<IUserProfileRepository, PostgresUserProfileRepository>();
        builder.Services.AddSingleton<MeetingSummaryComposer>();
        builder.Services.AddSingleton(sp => new MonzeApp(
            sp.GetRequiredService<IAuthorizationRepository>(),
            sp.GetRequiredService<IWelcomeRepository>(),
            sp.GetRequiredService<IRoleRepository>(),
            sp.GetRequiredService<IMeetingRepository>(),
            sp.GetRequiredService<ISchedulingRepository>(),
            sp.GetRequiredService<IAiUsageRepository>(),
            sp.GetRequiredService<IMessageHistoryRepository>(),
            sp.GetRequiredService<IWelcomeDraftStore>(),
            sp.GetService<IAiProvider>(),
            sp.GetRequiredService<IReadModelCache>(),
            sp.GetRequiredService<MonzeCommandOptions>(),
            sp.GetRequiredService<AiExecutionOptions>()));
        builder.Services.AddMemoryCache(cache => cache.SizeLimit = 64 * 1024 * 1024);
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

        if (!migrateOnly)
        {
            builder.Services.AddSingleton<ITranscriptClient>(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                var baseUrl = configuration["Mezon:AgentBaseUrl"];
                var transcriptBotId = configuration.GetValue<long>("Mezon:BotId");
                var transcriptBotToken = configuration["Mezon:Token"];
                if (string.IsNullOrWhiteSpace(baseUrl)
                    || transcriptBotId <= 0
                    || string.IsNullOrWhiteSpace(transcriptBotToken))
                {
                    return new DisabledTranscriptClient();
                }

                var http = new HttpClient
                {
                    BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
                    Timeout = TimeSpan.FromSeconds(30)
                };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Monze/1.0");
                return new HttpTranscriptClient(http, transcriptBotId, transcriptBotToken);
            });

            builder.Services.AddSingleton<StartupReadiness>();
            builder.Services.AddHostedService<StartupSchemaValidator>();
            builder.Services.AddHostedService<MonzeBot>();
            builder.Services.AddHostedService<MeetingMaintenanceWorker>();
        }

        options.ConfigureTestServices?.Invoke(builder.Services);
        return builder;
    }
}
