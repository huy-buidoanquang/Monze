using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Monze.Application;

namespace Monze;

public sealed class OpenAiCompatibleProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<OpenAiCompatibleProvider> _logger;

    public OpenAiCompatibleProvider(
        HttpClient http,
        string baseUrl,
        string apiKey,
        string model,
        ILogger<OpenAiCompatibleProvider> logger)
    {
        _http = http;
        _model = model;
        _logger = logger;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string?> CompleteAsync(string instruction, string input, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = _model,
            max_tokens = 8192,
            temperature = 0.2,
            enable_thinking = false,
            messages = new object[]
            {
                new { role = "system", content = instruction },
                new { role = "user", content = input }
            }
        });
        try
        {
            using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(
                "v1/chat/completions",
                requestContent,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "AI provider returned HTTP {StatusCode} with content type {ContentType}.",
                    (int)response.StatusCode,
                    response.Content.Headers.ContentType?.MediaType ?? "unknown");
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("AI provider returned unsupported content type {ContentType}.", mediaType);
                return null;
            }

            var responseText = await BoundedHttpContent.ReadStringAsync(
                response.Content,
                HttpPayloadLimits.AiResponseBytes,
                cancellationToken);
            if (responseText is null)
            {
                _logger.LogWarning("AI provider response exceeded the configured payload limit.");
                return null;
            }

            using var document = JsonDocument.Parse(responseText);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                _logger.LogWarning(
                    "AI provider returned JSON without choices; response length {ResponseLength}.",
                    responseText.Length);
                return null;
            }

            var message = choices[0].TryGetProperty("message", out var messageElement)
                ? messageElement
                : default;
            var result = message.ValueKind == JsonValueKind.Object &&
                   message.TryGetProperty("content", out var content) &&
                   content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(result))
            {
                _logger.LogWarning(
                    "AI provider returned an empty message content; response length {ResponseLength}.",
                    responseText.Length);
            }

            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "AI provider request failed at the HTTP layer.");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "AI provider returned invalid JSON.");
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("AI provider request timed out.");
            return null;
        }
    }
}
