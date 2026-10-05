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

    [Fact]
    public void Parses_voice_scope_from_ended_and_summary_payloads()
    {
        const string raw =
            "{\"room\":{\"room_id\":\"room-ended\",\"room_name\":\"2104288438869037056\"},\"timestamp\":\"2026-10-02T08:00:00Z\"}";

        Assert.True(AgentEventPayload.TryParse(raw, out var payload));
        Assert.Equal("room-ended", payload.RoomId);
        Assert.Equal(2104288438869037056L, payload.VoiceChannelId);
        Assert.Null(payload.ClanId);
    }
}
