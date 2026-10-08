using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Monze.Application;

namespace Monze;

public sealed class HttpTranscriptClient : ITranscriptClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly long _botId;
    private readonly string _botToken;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public HttpTranscriptClient(
        HttpClient http,
        long botId,
        string botToken,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _botId = botId;
        _botToken = botToken ?? throw new ArgumentNullException(nameof(botToken));
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<AgentSummaryResult?> FetchSummaryAsync(string roomId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return null;
        }

        using var operationTimeout = _http.Timeout == Timeout.InfiniteTimeSpan
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout?.CancelAfter(_http.Timeout);
        var operationToken = operationTimeout?.Token ?? cancellationToken;

        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var accessToken = await GetValidAccessTokenAsync(operationToken);
                    if (string.IsNullOrWhiteSpace(accessToken))
                    {
                        return null;
                    }

                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        $"api/v2/summary/room/id/{Uri.EscapeDataString(roomId)}");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    using var response = await _http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        operationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        var body = await BoundedHttpContent.ReadStringAsync(
                            response.Content,
                            HttpPayloadLimits.TranscriptResponseBytes,
                            operationToken);
                        if (body is null)
                        {
                            _logger.LogWarning(
                                "Transcript summary response exceeds {LimitBytes} bytes and was not read. RoomId={RoomId}.",
                                HttpPayloadLimits.TranscriptResponseBytes,
                                roomId);
                            return null;
                        }

                        return ExtractSummary(body, roomId);
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                    {
                        InvalidateAccessToken(accessToken);
                        continue;
                    }

                    if (!IsTransient(response.StatusCode) || attempt == 2)
                    {
                        return null;
                    }
                }
                catch (HttpRequestException) when (attempt < 2)
                {
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 2)
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), _time, operationToken);
            }
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return null;
    }

    internal static AgentSummaryResult? ExtractSummary(string json, string roomId)
        => AgentSummaryParser.TryParse(json, roomId, out var result) ? result : null;

    private async Task<string?> GetValidAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (IsAccessTokenValid())
        {
            return _accessToken;
        }

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (IsAccessTokenValid())
            {
                return _accessToken;
            }

            if (!string.IsNullOrWhiteSpace(_refreshToken))
            {
                var refreshed = await AuthenticateAsync(
                    "api/v2/auth/refresh",
                    JsonSerializer.Serialize(new { refresh_token = _refreshToken }),
                    cancellationToken);
                if (refreshed)
                {
                    return _accessToken;
                }
            }

            var loggedIn = await AuthenticateAsync(
                "api/v2/auth/mezon/bot/login",
                JsonSerializer.Serialize(new { account = new { appid = _botId.ToString(), token = _botToken } }),
                cancellationToken);
            return loggedIn ? _accessToken : null;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<bool> AuthenticateAsync(string path, string payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var body = await BoundedHttpContent.ReadStringAsync(
            response.Content,
            HttpPayloadLimits.TranscriptResponseBytes,
            cancellationToken);
        if (body is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!TryGetString(root, "access_token", out var accessToken)
                || !TryGetString(root, "refresh_token", out var refreshToken)
                || !root.TryGetProperty("expires_in", out var expiresIn)
                || !expiresIn.TryGetInt64(out var expiresInSeconds)
                || expiresInSeconds <= 0)
            {
                return false;
            }

            _accessToken = accessToken;
            _refreshToken = refreshToken;
            _accessTokenExpiresAt = _time.GetUtcNow().AddSeconds(expiresInSeconds);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool IsAccessTokenValid()
        => !string.IsNullOrWhiteSpace(_accessToken)
            && _time.GetUtcNow() < _accessTokenExpiresAt - TimeSpan.FromSeconds(30);

    private void InvalidateAccessToken(string token)
    {
        if (string.Equals(_accessToken, token, StringComparison.Ordinal))
        {
            _accessToken = null;
            _accessTokenExpiresAt = DateTimeOffset.MinValue;
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public void Dispose() => _tokenGate.Dispose();

    private static bool IsTransient(System.Net.HttpStatusCode statusCode)
        => statusCode is System.Net.HttpStatusCode.RequestTimeout
            or System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout;
}
