using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Monze.Application;

/// <summary>
/// Produces the durable identity used by the Agent inbox. The event id from the
/// Agent contract is preferred; the payload hash remains a compatibility fallback
/// for older Agent deployments that did not include it.
/// </summary>
public static class AgentEventIdentity
{
    public static string Compute(byte kind, string eventType, string rawResponse)
    {
        if (TryReadEventId(rawResponse, out var eventId))
        {
            return "event:" + kind.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + eventId;
        }

        var material = string.Concat(
            kind.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "\n",
            eventType,
            "\n",
            rawResponse);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public static bool TryReadEventId(string rawResponse, out string eventId)
    {
        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            var root = document.RootElement;
            if (root.TryGetProperty("event_id", out var property)
                || root.TryGetProperty("eventId", out property))
            {
                if (property.ValueKind == JsonValueKind.String)
                {
                    eventId = property.GetString() ?? string.Empty;
                    return !string.IsNullOrWhiteSpace(eventId);
                }

                if (property.ValueKind == JsonValueKind.Number)
                {
                    eventId = property.GetRawText();
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        eventId = string.Empty;
        return false;
    }
}
