using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Monze.Tests;

public sealed class OpenAiCompatibleProviderTests
{
    [Fact]
    public async Task Sends_openai_compatible_request_and_reads_content()
    {
        string? authorization = null;
        string? requestJson = null;
        using var http = new HttpClient(new OpenAiRecordingHandler(async request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}");
        }));
        var provider = new OpenAiCompatibleProvider(
            http,
            "https://provider.test",
            "test-key",
            "test-model",
            NullLogger<OpenAiCompatibleProvider>.Instance);

        var result = await provider.CompleteAsync("instruction", "input", CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal("Bearer test-key", authorization);
        using var document = JsonDocument.Parse(requestJson!);
        Assert.Equal("test-model", document.RootElement.GetProperty("model").GetString());
        Assert.False(document.RootElement.GetProperty("enable_thinking").GetBoolean());
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("instruction", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("input", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Returns_null_for_empty_content()
    {
        using var http = new HttpClient(new OpenAiRecordingHandler(_ => Task.FromResult(
            JsonResponse("{\"choices\":[{\"message\":{\"content\":\"\"}}]}"))));
        var provider = CreateProvider(http);

        var result = await provider.CompleteAsync("instruction", "input", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Returns_null_for_http_failure()
    {
        using var http = new HttpClient(new OpenAiRecordingHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadGateway))));
        var provider = CreateProvider(http);

        var result = await provider.CompleteAsync("instruction", "input", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Returns_null_for_invalid_json()
    {
        using var http = new HttpClient(new OpenAiRecordingHandler(_ => Task.FromResult(
            JsonResponse("not-json"))));
        var provider = CreateProvider(http);

        var result = await provider.CompleteAsync("instruction", "input", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Returns_null_when_the_http_client_times_out()
    {
        using var http = new HttpClient(new OpenAiRecordingHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable after cancellation.");
            }))
        {
            Timeout = TimeSpan.FromMilliseconds(50)
        };
        var provider = CreateProvider(http);

        var result = await provider.CompleteAsync("instruction", "input", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Propagates_caller_cancellation()
    {
        using var http = new HttpClient(new OpenAiRecordingHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable after cancellation.");
            }));
        var provider = CreateProvider(http);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.CompleteAsync("instruction", "input", cancellation.Token));
    }

    private static OpenAiCompatibleProvider CreateProvider(HttpClient http)
        => new(
            http,
            "https://provider.test",
            "test-key",
            "test-model",
            NullLogger<OpenAiCompatibleProvider>.Instance);

    private static HttpResponseMessage JsonResponse(string content)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

}
