using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monze.Hosting;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Hosting;

public sealed class MonzeHostCompositionTests : IDisposable
{
    private const string UpdateSnapshotVariable = "MONZE_UPDATE_SNAPSHOTS";
    private const string SnapshotSchema = "monze.composition.v1";

    private readonly string _contentRoot = Directory.CreateTempSubdirectory("monze-composition-").FullName;

    public void Dispose() => Directory.Delete(_contentRoot, recursive: true);

    [Fact]
    [Req("REQ-HOST-001")]
    public void Production_registrations_match_the_committed_snapshot()
    {
        var actual = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["run"] = DescribeRegistrations([], ai: false),
            ["run-with-ai"] = DescribeRegistrations([], ai: true),
            ["migrate"] = DescribeRegistrations(["migrate"], ai: false)
        };
        var path = Path.Combine(RepositoryPaths.Root, "tests", "traceability", "composition-snapshot.json");
        if (Environment.GetEnvironmentVariable(UpdateSnapshotVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(
                new { schema = SnapshotSchema, variants = actual },
                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(path, json.ReplaceLineEndings("\n") + "\n");
        }

        Assert.True(File.Exists(path), $"Missing {path}; run once with {UpdateSnapshotVariable}=1 and review the diff.");
        using var snapshot = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(SnapshotSchema, snapshot.RootElement.GetProperty("schema").GetString());
        var expected = snapshot.RootElement.GetProperty("variants");
        Assert.Equal(
            actual.Keys,
            expected.EnumerateObject().Select(variant => variant.Name).Order(StringComparer.Ordinal));
        foreach (var (variant, registrations) in actual)
        {
            var expectedRegistrations = expected.GetProperty(variant)
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray();
            Assert.Equal(expectedRegistrations, registrations);
        }
    }

    [Fact]
    [Req("REQ-HOST-002")]
    public void Hosted_services_start_in_validation_bot_maintenance_order()
    {
        var builder = CreateBuilder([], ai: false);

        var hostedServices = builder.Services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .ToArray();

        Assert.Equal(
            new Type?[] { typeof(StartupSchemaValidator), typeof(MonzeBot), typeof(MeetingMaintenanceWorker) },
            hostedServices);
        Assert.Contains(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(StartupReadiness)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    [Req("REQ-HOST-010", "REQ-TIME-001")]
    public void Production_clock_is_the_system_time_provider()
    {
        var builder = CreateBuilder([], ai: false);

        var descriptor = Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(TimeProvider));

        Assert.Same(TimeProvider.System, descriptor.ImplementationInstance);
        DisposeInstances(builder.Services);
    }

    [Fact]
    [Req("REQ-HOST-003")]
    public void Migrate_composition_registers_no_hosted_services()
    {
        var builder = CreateBuilder(["migrate"], ai: false);

        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    [Req("REQ-HOST-004", "REQ-SEC-100")]
    public void Test_composition_never_adds_settings_files()
    {
        var builder = CreateBuilder([], ai: false);

        Assert.DoesNotContain(builder.Configuration.Sources, source => source is JsonConfigurationSource);
    }

    [Fact]
    [Req("REQ-HOST-005")]
    public void Settings_files_are_added_after_earlier_sources_and_override_them()
    {
        var builder = MonzeHostComposition.CreateBuilder([], new MonzeHostCompositionOptions
        {
            Settings = TestSettings(),
            Configuration = BaseConfiguration(),
            AddProductionLogging = false
        });

        // JSON files are the last sources, so they override environment
        // variables and command-line values (documented in CLAUDE.md).
        var lastSources = builder.Configuration.Sources.TakeLast(3).ToArray();
        Assert.All(lastSources, source => Assert.IsType<JsonConfigurationSource>(source));
        Assert.Equal(
            new string?[] { "appsettings.json", "appsettings.Production.local.json", "appsettings.secrets.json" },
            lastSources.Cast<JsonConfigurationSource>().Select(source => source.Path));
        Assert.Contains(builder.Configuration.Sources, source =>
            source is MemoryConfigurationSource memory
            && memory.InitialData?.Any(entry => entry.Key == "Monze:Postgres") == true);
    }

    [Fact]
    [Req("REQ-HOST-006")]
    public void Test_service_overrides_run_after_production_registrations()
    {
        var replacement = new StartupReadiness();
        var builder = MonzeHostComposition.CreateBuilder([], new MonzeHostCompositionOptions
        {
            Settings = TestSettings(),
            LoadAppSettingsFiles = false,
            Configuration = BaseConfiguration(),
            AddProductionLogging = false,
            ConfigureTestServices = services => services.AddSingleton(replacement)
        });

        using var provider = builder.Services.BuildServiceProvider();

        Assert.Same(replacement, provider.GetRequiredService<StartupReadiness>());
    }

    [Theory]
    [Req("REQ-HOST-007")]
    [InlineData(new[] { "migrate" }, true)]
    [InlineData(new[] { "MIGRATE" }, true)]
    [InlineData(new[] { "--Monze:Commands:Prefix=!", "migrate" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "migration" }, false)]
    public void Migrate_only_is_selected_by_the_migrate_argument(string[] args, bool expected)
    {
        Assert.Equal(expected, MonzeHostComposition.IsMigrateOnly(args));
    }

    [Fact]
    [Req("REQ-HOST-008")]
    public void Composition_without_postgres_fails_before_building()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MonzeHostComposition.CreateBuilder([], new MonzeHostCompositionOptions
            {
                Settings = TestSettings(),
                LoadAppSettingsFiles = false,
                Configuration = [new("Monze:Logging:Directory", Path.Combine(_contentRoot, "logs"))],
                AddProductionLogging = false
            }));

        Assert.Equal("Set Monze:Postgres before starting Monze.", error.Message);
    }

    [Fact]
    [Req("REQ-HOST-009")]
    public async Task Readiness_completes_once_and_ignores_later_signals()
    {
        var ready = new StartupReadiness();
        ready.MarkReady();
        ready.MarkFailed(new InvalidOperationException("late"));
        await ready.Ready;

        var failed = new StartupReadiness();
        failed.MarkFailed(new InvalidOperationException("schema"));
        failed.MarkReady();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.Ready);
        Assert.Equal("schema", error.Message);
    }

    private IReadOnlyList<string> DescribeRegistrations(string[] args, bool ai)
    {
        var baseline = Host.CreateApplicationBuilder(TestSettings()).Services
            .Select(Describe)
            .GroupBy(entry => entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var builder = CreateBuilder(args, ai);
        var added = new List<string>();
        foreach (var descriptor in builder.Services)
        {
            var entry = Describe(descriptor);
            if (baseline.TryGetValue(entry, out var remaining) && remaining > 0)
            {
                baseline[entry] = remaining - 1;
                continue;
            }

            added.Add(entry);
        }

        DisposeInstances(builder.Services);
        return added;
    }

    private HostApplicationBuilder CreateBuilder(string[] args, bool ai)
    {
        var configuration = BaseConfiguration().ToList();
        if (ai)
        {
            configuration.Add(new("Monze:Ai:BaseUrl", "http://127.0.0.1:9/"));
            configuration.Add(new("Monze:Ai:ApiKey", "composition-snapshot"));
        }

        return MonzeHostComposition.CreateBuilder(args, new MonzeHostCompositionOptions
        {
            Settings = TestSettings(),
            LoadAppSettingsFiles = false,
            Configuration = configuration
        });
    }

    private HostApplicationBuilderSettings TestSettings() => new()
    {
        DisableDefaults = true,
        ApplicationName = "Monze",
        EnvironmentName = Environments.Production,
        ContentRootPath = _contentRoot
    };

    private KeyValuePair<string, string?>[] BaseConfiguration() =>
    [
        new("Monze:Postgres", "Host=127.0.0.1;Port=1;Database=monze_composition;Username=monze"),
        new("Monze:Logging:Directory", Path.Combine(_contentRoot, "logs"))
    ];

    private static string Describe(ServiceDescriptor descriptor)
    {
        string implementation;
        if (descriptor.IsKeyedService)
        {
            implementation = descriptor.KeyedImplementationType is { } keyedType
                ? Name(keyedType)
                : descriptor.KeyedImplementationInstance is { } keyedInstance
                    ? "instance " + Name(keyedInstance.GetType())
                    : "factory";
            implementation += $" [key {descriptor.ServiceKey}]";
        }
        else
        {
            implementation = descriptor.ImplementationType is { } type
                ? Name(type)
                : descriptor.ImplementationInstance is { } instance
                    ? "instance " + Name(instance.GetType())
                    : "factory";
        }

        return $"{descriptor.Lifetime} {Name(descriptor.ServiceType)} <- {implementation}";
    }

    private static string Name(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var definition = type.GetGenericTypeDefinition().FullName ?? type.Name;
        var tick = definition.IndexOf('`', StringComparison.Ordinal);
        var arguments = type.IsGenericTypeDefinition
            ? string.Join(",", type.GetGenericArguments().Select(_ => string.Empty))
            : string.Join(", ", type.GetGenericArguments().Select(Name));
        return $"{definition[..tick]}<{arguments}>";
    }

    private static void DisposeInstances(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (!descriptor.IsKeyedService && descriptor.ImplementationInstance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
