using Monze.Application;
using Xunit;

namespace Monze.Tests;

public sealed class AgentEventPayloadTests
{
    [Fact]
    public void Parses_nested_room_and_numeric_voice_fields()
    {
        var ok = AgentEventPayload.TryParse(
            "{\"room\":{\"room_id\":\"room-42\",\"room_name\":\"2104288438869037056\"},\"clan_id\":2104288434238525440}",
            out var payload);

        Assert.True(ok);
        Assert.Equal("room-42", payload.RoomId);
        Assert.Equal(2104288438869037056L, payload.VoiceChannelId);
        Assert.Equal(2104288434238525440L, payload.ClanId);
    }

    [Fact]
    public void Parses_flat_fields_and_rejects_invalid_json_without_throwing()
    {
        Assert.True(AgentEventPayload.TryParse(
            "{\"room_id\":\"room-7\",\"voice_channel_id\":\"42\"}",
            out var payload));
        Assert.Equal("room-7", payload.RoomId);
        Assert.Equal(42L, payload.VoiceChannelId);
        Assert.Null(payload.ClanId);

        Assert.False(AgentEventPayload.TryParse("not-json", out _));
    }
}
