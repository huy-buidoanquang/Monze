using Monze.Application;

namespace Monze;

public sealed class HttpTranscriptClient : ITranscriptClient
{
    private readonly HttpClient _http;

    public HttpTranscriptClient(HttpClient http) => _http = http;

    public async Task<string?> FetchSummaryAsync(string roomId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(
                    $"api/v2/summary/room/id/{Uri.EscapeDataString(roomId)}",
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return await BoundedHttpContent.ReadStringAsync(
                        response.Content,
                        HttpPayloadLimits.TranscriptResponseBytes,
                        cancellationToken);
                }

                if (!IsTransient(response.StatusCode) || attempt == 1)
                {
                    return null;
                }
            }
            catch (HttpRequestException) when (attempt == 0)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt == 0)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
        }

        return null;
    }

    private static bool IsTransient(System.Net.HttpStatusCode statusCode)
        => statusCode is System.Net.HttpStatusCode.RequestTimeout
            or System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout;
}

