using Mezon.Net.Sdk.Agent;

namespace Monze;

internal readonly record struct AgentIngressItem(
    AgentSseSessionEvent Event,
    AgentEventKind Kind);
