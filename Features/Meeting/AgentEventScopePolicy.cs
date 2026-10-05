using Mezon.Net.Core;

namespace Monze;

internal static class AgentEventScopePolicy
{
    internal static bool Allows(
        long? payloadClanId,
        long resolvedClanId,
        int channelType,
        IReadOnlySet<long> knownClanIds)
        => channelType == (int)ChannelType.MezonVoice
            && knownClanIds.Contains(resolvedClanId)
            && (payloadClanId is null || payloadClanId == resolvedClanId);
}
