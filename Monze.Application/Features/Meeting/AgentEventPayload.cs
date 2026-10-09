using System.Globalization;
using System.Text.Json;

namespace Monze.Application;

public readonly record struct AgentEventPayload(
    string? RoomId,
    long? VoiceChannelId,
    long? ClanId)
{
    public static bool TryParse(string rawResponse, out AgentEventPayload payload)
    {
        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                // Valid JSON that is not an object (an array, a number) carries no event fields (CAND-01).
                payload = default;
                return true;
            }

            var roomId = TryReadRoomId(root);
            var voiceChannelId = TryReadVoiceChannelId(root);
            var clanId = TryReadPositiveLong(root, "clan_id");
            payload = new AgentEventPayload(roomId, voiceChannelId, clanId);
            return true;
        }
        catch (JsonException)
        {
            payload = default;
            return false;
        }
    }

    private static string? TryReadRoomId(JsonElement root)
    {
        if (root.TryGetProperty("room_id", out var room))
        {
            return ReadScalarText(room);
        }

        if (root.TryGetProperty("room", out var nested)
            && nested.ValueKind == JsonValueKind.Object
            && nested.TryGetProperty("room_id", out var nestedId))
        {
            return ReadScalarText(nestedId);
        }

        return null;
    }

    private static long? TryReadVoiceChannelId(JsonElement root)
    {
        if (root.TryGetProperty("voice_channel_id", out var voice)
            && TryReadPositiveLong(voice, out var voiceId))
        {
            return voiceId;
        }

        if (root.TryGetProperty("room", out var room)
            && room.ValueKind == JsonValueKind.Object
            && room.TryGetProperty("room_name", out var name)
            && TryReadPositiveLong(name, out var roomVoiceId))
        {
            return roomVoiceId;
        }

        return null;
    }

    private static long? TryReadPositiveLong(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value)
            && TryReadPositiveLong(value, out var result)
            ? result
            : null;

    private static bool TryReadPositiveLong(JsonElement value, out long result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result))
        {
            return result > 0;
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out result))
        {
            return result > 0;
        }

        result = 0;
        return false;
    }

    private static string? ReadScalarText(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var number)
                => number.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
}
