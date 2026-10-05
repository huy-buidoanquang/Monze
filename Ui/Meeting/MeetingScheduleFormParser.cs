using System.Globalization;
using System.Text.Json;
using Monze.Domain;

namespace Monze.Ui;

public static class MeetingScheduleFormParser
{
    public static string DescribeInput(string? extraData)
        => string.Join(
            ", ",
            DescribeField("name", Read(extraData, MeetingButtonId.ScheduleName)),
            DescribeField("date", Read(extraData, MeetingButtonId.ScheduleDate)),
            DescribeField("time", Read(extraData, MeetingButtonId.ScheduleTime)),
            DescribeField("frequency", Read(extraData, MeetingButtonId.ScheduleFrequency)));

    public static bool TryRead(
        string? extraData,
        out string name,
        out string date,
        out string time,
        out MeetingScheduleKind kind,
        out string error)
    {
        name = Read(extraData, MeetingButtonId.ScheduleName)?.Trim() ?? string.Empty;
        date = NormalizeDate(Read(extraData, MeetingButtonId.ScheduleDate));
        time = Read(extraData, MeetingButtonId.ScheduleTime)?.Trim() ?? string.Empty;
        var frequency = Read(extraData, MeetingButtonId.ScheduleFrequency);
        kind = frequency?.ToLowerInvariant() switch
        {
            "daily" => MeetingScheduleKind.Daily,
            "weekly" => MeetingScheduleKind.Weekly,
            _ => MeetingScheduleKind.Once
        };

        if (name.Length == 0 || name.Length > 120)
        {
            error = "Tên cuộc họp phải có từ 1 đến 120 ký tự.";
            return false;
        }

        if (!DateTime.TryParseExact(
                date,
                "dd/MM/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            error = "Hãy chọn ngày hợp lệ theo dạng dd/MM/yyyy.";
            return false;
        }

        if (!TimeOnly.TryParseExact(
                time,
                ["HH:mm", "H:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            error = "Giờ phải theo dạng HH:mm.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim();
        if (DateTime.TryParseExact(
                text,
                "dd/MM/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localDate))
        {
            return localDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParseExact(
                text,
                ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssK"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out localDate))
        {
            return localDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }

        // A date picker can be serialized as an ISO timestamp by the client.
        // Keep the calendar date from the payload instead of applying a UTC
        // conversion that could move it to an adjacent day.
        if (text.Length >= 10
            && DateTime.TryParseExact(
                text[..10],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out localDate))
        {
            return localDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }

        return text;
    }

    private static string? Read(string? extraData, string id)
    {
        if (string.IsNullOrWhiteSpace(extraData))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(extraData);
            return Find(document.RootElement, id);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DescribeField(string name, string? value)
    {
        if (value is null)
        {
            return $"{name}=missing";
        }

        var shape = name switch
        {
            "date" when value.Length >= 10 && value[4] == '-' && value[7] == '-' => "iso-date",
            "date" when value.Length >= 10 && value[2] == '/' && value[5] == '/' => "local-date",
            "time" when value.Contains(':', StringComparison.Ordinal) => "clock",
            _ => "text"
        };
        return $"{name}=length:{value.Length},shape:{shape}";
    }

    private static string? Find(JsonElement element, string id)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(id, out var direct))
            {
                return Scalar(direct);
            }

            if (element.TryGetProperty(id + "-component", out var nestedDirect))
            {
                return Scalar(nestedDirect);
            }

            if (element.TryGetProperty("id", out var componentId)
                && componentId.ValueKind == JsonValueKind.String
                && (string.Equals(componentId.GetString(), id, StringComparison.Ordinal)
                    || string.Equals(componentId.GetString(), id + "-component", StringComparison.Ordinal))
                && element.TryGetProperty("value", out var value))
            {
                return Scalar(value);
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = Find(property.Value, id);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = Find(item, id);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? Scalar(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object when element.TryGetProperty("value", out var value) => Scalar(value),
            JsonValueKind.Object when element.TryGetProperty("values", out var values) => Scalar(values),
            JsonValueKind.Array => FirstScalar(element),
            _ => null
        };

    private static string? FirstScalar(JsonElement element)
    {
        foreach (var item in element.EnumerateArray())
        {
            var value = Scalar(item);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
