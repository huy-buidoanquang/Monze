using Monze.Application;
using Xunit;

namespace Monze.Tests;

public sealed class AgentEventIdentityTests
{
    [Fact]
    public void Uses_contract_event_id_when_payload_metadata_changes()
    {
        const string first = "{\"event_id\":\"agent-42\",\"event_type\":\"room_started\",\"timestamp\":1}";
        const string retry = "{\"event_id\":\"agent-42\",\"event_type\":\"room_started\",\"timestamp\":2}";

        Assert.Equal(
            AgentEventIdentity.Compute(1, "room_started", first),
            AgentEventIdentity.Compute(1, "room_started", retry));
    }

    [Fact]
    public void Falls_back_to_payload_hash_for_legacy_event()
    {
        var first = AgentEventIdentity.Compute(1, "room_started", "{\"room\":{\"room_id\":\"r1\"}}");
        var second = AgentEventIdentity.Compute(1, "room_started", "{\"room\":{\"room_id\":\"r2\"}}");

        Assert.NotEqual(first, second);
        Assert.DoesNotContain("r1", first, StringComparison.Ordinal);
    }
}
