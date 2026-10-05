using Mezon.Net.Core;
using Xunit;

namespace Monze.Tests;

public sealed class AgentEventScopePolicyTests
{
    private static readonly IReadOnlySet<long> KnownClans = new HashSet<long> { 42 };

    [Theory]
    [InlineData(null, 42, (int)ChannelType.MezonVoice, true)]
    [InlineData(42L, 42, (int)ChannelType.MezonVoice, true)]
    [InlineData(7L, 42, (int)ChannelType.MezonVoice, false)]
    [InlineData(null, 7, (int)ChannelType.MezonVoice, false)]
    [InlineData(null, 42, (int)ChannelType.Channel, false)]
    public void Allows_only_voice_channels_in_a_known_matching_clan(
        long? payloadClanId,
        long resolvedClanId,
        int channelType,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgentEventScopePolicy.Allows(
                payloadClanId,
                resolvedClanId,
                channelType,
                KnownClans));
    }

    [Theory]
    [InlineData(MezonStatusCode.InvalidArgument, true)]
    [InlineData(MezonStatusCode.NotFound, true)]
    [InlineData(MezonStatusCode.PermissionDenied, true)]
    [InlineData(MezonStatusCode.FailedPrecondition, true)]
    [InlineData(MezonStatusCode.Unimplemented, true)]
    [InlineData(MezonStatusCode.DeadlineExceeded, false)]
    [InlineData(MezonStatusCode.ResourceExhausted, false)]
    [InlineData(MezonStatusCode.Internal, false)]
    [InlineData(MezonStatusCode.Unavailable, false)]
    public void Terminal_channel_lookup_statuses_are_not_retried(
        MezonStatusCode statusCode,
        bool expected)
    {
        Assert.Equal(expected, MonzeBot.IsTerminalAgentChannelLookup(statusCode));
    }
}
