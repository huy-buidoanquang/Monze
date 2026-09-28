using System.Text.Json;
using Monze.Application;
using Monze.Application.Commands;

namespace Monze.Ui;

public static class MonzeWelcomeFormParser
{
    public static bool ReadEnabled(string? extraData, bool fallback)
        => Read(extraData, MonzeButtonId.WelcomeStatus) is { } value
            ? value.Equals("on", StringComparison.OrdinalIgnoreCase)
                ? true
                : value.Equals("off", StringComparison.OrdinalIgnoreCase)
                    ? false
                    : fallback
            : fallback;

    public static WelcomeEmbedSettings ReadEmbed(string? extraData, WelcomeSettings? current)
    {
        var fallback = current?.Embed ?? new WelcomeEmbedSettings(
            Title: "Chào mừng",
            Description: current?.Text ?? MonzeMessages.DefaultWelcomeText);
        return new WelcomeEmbedSettings(
            ReadOrDefault(extraData, MonzeButtonId.WelcomeTitle, fallback.Title),
            ReadOrDefault(extraData, MonzeButtonId.WelcomeDescription, fallback.Description),
            ReadOrDefault(extraData, MonzeButtonId.WelcomeUrl, fallback.Url),
            ReadOrDefault(extraData, MonzeButtonId.WelcomeColor, fallback.Color),
            ReadOrDefault(extraData, MonzeButtonId.WelcomeAuthor, fallback.AuthorName),
            ReadOrDefault(extraData, MonzeButtonId.WelcomeAuthorIcon, fallback.AuthorIconUrl),
            ReadOrDefault(extraData, MonzeButtonId.WelcomeAuthorUrl, fallback.AuthorUrl),
            ReadOrDefault(extraData, MonzeButtonId.WelcomeThumbnail, fallback.ThumbnailUrl),
            ReadOrDefault(extraData, MonzeButtonId.WelcomeImage, fallback.ImageUrl),
            ReadOrDefault(extraData, MonzeButtonId.WelcomeFooter, fallback.FooterText),
            ReadOrDefault(extraData, MonzeButtonId.WelcomeFooterIcon, fallback.FooterIconUrl),
            ReadOrDefault(extraData, MonzeButtonId.WelcomeFieldName, fallback.FieldName),
            ReadOrDefault(extraData, MonzeButtonId.WelcomeFieldValue, fallback.FieldValue));
    }

    private static string? ReadOrDefault(string? extraData, string id, string? fallback)
        => Read(extraData, id) ?? fallback;

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

    private static string? Find(JsonElement element, string id)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(id, out var direct))
            {
                return Scalar(direct);
            }

            if (element.TryGetProperty("id", out var componentId)
                && componentId.ValueKind == JsonValueKind.String
                && string.Equals(componentId.GetString(), id, StringComparison.Ordinal)
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
            JsonValueKind.Array => element.EnumerateArray()
                .Select(Scalar)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
            _ => null
        };
}
