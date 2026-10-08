using System.Globalization;
using System.Text.Json;

namespace Monze.Application;

public static class AgentSummaryParser
{
    public static bool TryParse(
        string json,
        string roomId,
        out AgentSummaryResult result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(roomId))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            JsonElement summary;
            if (root.ValueKind == JsonValueKind.Object
                && HasRoomId(root, roomId))
            {
                summary = root;
            }
            else if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("data", out var data))
            {
                summary = data.ValueKind == JsonValueKind.Array
                    ? FindRoom(data, roomId)
                    : data;
            }
            else
            {
                return false;
            }

            if (summary.ValueKind != JsonValueKind.Object
                || !HasRoomId(summary, roomId)
                || !summary.TryGetProperty("summary_data", out var summaryData)
                || summaryData.ValueKind != JsonValueKind.Object
                || !summaryData.TryGetProperty("summary", out var summaryText)
                || summaryText.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(summaryText.GetString()))
            {
                return false;
            }

            var actionItems = summaryData.TryGetProperty("action_items", out var actionItemsElement)
                ? ReadActionItems(actionItemsElement)
                : [];
            result = new AgentSummaryResult(
                roomId,
                summaryText.GetString()!.Trim(),
                ReadString(summary, "full_text"),
                ReadTimestamp(summary, "created_at"),
                ReadTimestamp(summary, "finalized_at") ?? ReadTimestamp(summary, "completed_at"),
                ReadParticipants(summary),
                ReadSpeechDurations(summary),
                actionItems,
                summary.GetRawText());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement FindRoom(JsonElement data, string roomId)
    {
        foreach (var item in data.EnumerateArray())
        {
            if (HasRoomId(item, roomId))
            {
                return item;
            }
        }

        return default;
    }

    // Elements of an unexpected kind (a number where an object belongs) are
    // skipped like missing ones instead of throwing (CAND-11).
    private static bool HasRoomId(JsonElement element, string roomId)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("room_id", out var value)
            && value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), roomId, StringComparison.Ordinal);

    private static string? ReadString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return value is not null
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp)
            ? timestamp
            : null;
    }

    private static IReadOnlyList<string> ReadParticipants(JsonElement summary)
    {
        if (!summary.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var participant in participants.EnumerateArray())
        {
            var identity = participant.ValueKind == JsonValueKind.String
                ? participant.GetString()
                : ReadString(participant, "participant_identity") ?? ReadString(participant, "id");
            if (!string.IsNullOrWhiteSpace(identity))
            {
                result.Add(identity.Trim());
            }
        }

        return result;
    }

    private static IReadOnlyList<AgentSpeechDuration> ReadSpeechDurations(JsonElement summary)
    {
        if (!summary.TryGetProperty("speech_durations", out var speechDurations)
            || speechDurations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<AgentSpeechDuration>();
        foreach (var item in speechDurations.EnumerateArray())
        {
            var identity = ReadString(item, "participant_identity") ?? ReadString(item, "participant_id");
            if (string.IsNullOrWhiteSpace(identity)
                || !TryReadDouble(item, "duration", out var duration)
                || duration < 0)
            {
                continue;
            }

            result.Add(new AgentSpeechDuration(identity.Trim(), duration));
        }

        return result;
    }

    private static IReadOnlyList<AgentActionItemGroup> ReadActionItems(JsonElement actionItems)
    {
        if (actionItems.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<AgentActionItemGroup>();
        foreach (var property in actionItems.EnumerateObject())
        {
            var items = new List<string>();
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        items.Add(item.GetString()!.Trim());
                    }
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                items.Add(property.Value.GetString()!.Trim());
            }

            if (items.Count > 0)
            {
                result.Add(new AgentActionItemGroup(property.Name, items));
            }
        }

        return result;
    }

    private static bool TryReadDouble(JsonElement element, string propertyName, out double value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String
                && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }
}
