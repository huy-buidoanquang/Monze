using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Monze.Hosting;

/// <summary>
/// Options for <see cref="MonzeHostComposition"/>. Production uses
/// <see cref="Production"/>, which reproduces the former Program.Main exactly.
/// Tests use in-memory configuration, no settings files and service overrides,
/// so a test host can never read appsettings.json.
/// </summary>
internal sealed class MonzeHostCompositionOptions
{
    public static MonzeHostCompositionOptions Production { get; } = new();

    /// <summary>Builder settings; null uses Host.CreateApplicationBuilder(args).</summary>
    public HostApplicationBuilderSettings? Settings { get; init; }

    /// <summary>Whether appsettings*.json files are added after the host defaults.</summary>
    public bool LoadAppSettingsFiles { get; init; } = true;

    /// <summary>In-memory configuration added before anything reads configuration.</summary>
    public IEnumerable<KeyValuePair<string, string?>>? Configuration { get; init; }

    /// <summary>Whether the console and daily file logger providers are added.</summary>
    public bool AddProductionLogging { get; init; } = true;

    /// <summary>Runs last, so tests can replace any production registration.</summary>
    public Action<IServiceCollection>? ConfigureTestServices { get; init; }
}
