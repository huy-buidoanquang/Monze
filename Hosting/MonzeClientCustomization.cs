using Mezon.Net.Sdk;

namespace Monze;

/// <summary>
/// Optional hook applied to the Mezon client options after Monze has built
/// them from configuration. Production registers none; tests use it to plug
/// in a simulated transport, REST client or rate-limit callback.
/// </summary>
public sealed class MonzeClientCustomization(Action<MezonClientOptions> configure)
{
    public Action<MezonClientOptions> Configure { get; } = configure;
}
