using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Monze.Testing;
using Xunit;

namespace Monze.Tests;

public sealed class HttpTranscriptClientTests
{
    [Fact]
    public void ExtractSummary_accepts_single_object_and_matching_array_item()
    {
        const string summary = "Nội dung đã tóm tắt.";
        var single = "{\"status\":\"ok\",\"data\":{\"room_id\":\"room-1\",\"summary_data\":{\"summary\":\"Nội dung đã tóm tắt.\"}}}";
        var array = "{\"status\":\"ok\",\"data\":[{\"room_id\":\"other\",\"summary_data\":{\"summary\":\"sai\"}},{\"room_id\":\"room-1\",\"summary_data\":{\"summary\":\"Nội dung đã tóm tắt.\"}}]}";

        Assert.Equal(summary, Monze.HttpTranscriptClient.ExtractSummary(single, "room-1")?.Summary);
        Assert.Equal(summary, Monze.HttpTranscriptClient.ExtractSummary(array, "room-1")?.Summary);
        Assert.Null(Monze.HttpTranscriptClient.ExtractSummary(single, "room-2"));
    }

    [Fact]
    public void ExtractSummary_accepts_the_room_object_persisted_in_meeting_summary()
    {
        const string stored = """
            {
              "room_id":"room-1",
              "created_at":"2026-10-01T10:00:00Z",
              "finalized_at":"2026-10-01T10:10:00Z",
              "summary_data":{"summary":"đã lưu"}
            }
            """;

        var result = Monze.HttpTranscriptClient.ExtractSummary(stored, "room-1");

        Assert.Equal("đã lưu", result?.Summary);
        Assert.Null(Monze.HttpTranscriptClient.ExtractSummary(stored, "room-2"));
    }

    [Fact]
    public async Task FetchSummary_logs_in_sends_bearer_and_preserves_transcript_data()
    {
        var handler = new HttpTranscriptRecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/auth/mezon/bot/login" => Json(HttpStatusCode.OK, "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600}"),
            "/api/v2/summary/room/id/room-1" => AuthorizedSummary(request, "access-1"),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://agent.test/") };
        using var client = new Monze.HttpTranscriptClient(http, 123, "bot-token");

        var result = await client.FetchSummaryAsync("room-1", CancellationToken.None);

        Assert.Equal("đã xong", result?.Summary);
        Assert.Equal("không trả ra", result?.FullText);
        Assert.Contains("room-1", result?.FullTranscriptJson, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
        using var loginBody = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[0]!);
        Assert.Equal("123", loginBody.RootElement.GetProperty("account").GetProperty("appid").GetString());
        Assert.Equal("Bearer", handler.Requests[1].Headers.Authorization?.Scheme);
        Assert.Equal("access-1", handler.Requests[1].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task FetchSummary_refreshes_after_unauthorized_response()
    {
        var summaryCalls = 0;
        var handler = new HttpTranscriptRecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/auth/mezon/bot/login" => Json(HttpStatusCode.OK, "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600}"),
            "/api/v2/auth/refresh" => Json(HttpStatusCode.OK, "{\"access_token\":\"access-2\",\"refresh_token\":\"refresh-2\",\"expires_in\":3600}"),
            "/api/v2/summary/room/id/room-1" when Interlocked.Increment(ref summaryCalls) == 1 => Json(HttpStatusCode.Unauthorized, "{}"),
            "/api/v2/summary/room/id/room-1" => AuthorizedSummary(request, "access-2"),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://agent.test/") };
        using var client = new Monze.HttpTranscriptClient(http, 123, "bot-token");

        var result = await client.FetchSummaryAsync("room-1", CancellationToken.None);

        Assert.Equal("đã xong", result?.Summary);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("/api/v2/auth/refresh", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.Equal("access-2", handler.Requests[3].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task FetchSummary_returns_null_when_the_total_http_timeout_elapses()
    {
        var handler = new HttpTranscriptTimeoutHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://agent.test/"),
            Timeout = TimeSpan.FromMilliseconds(50)
        };
        using var client = new Monze.HttpTranscriptClient(http, 123, "bot-token");

        var result = await client.FetchSummaryAsync("room-1", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchSummary_propagates_caller_cancellation()
    {
        var handler = new HttpTranscriptTimeoutHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://agent.test/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        using var client = new Monze.HttpTranscriptClient(http, 123, "bot-token");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.FetchSummaryAsync("room-1", cancellation.Token));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchSummary_retries_transient_transport_failures_then_recovers()
    {
        var summaryCalls = 0;
        var handler = new HttpTranscriptRecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/auth/mezon/bot/login" => Json(HttpStatusCode.OK, "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600}"),
            "/api/v2/summary/room/id/room-1" when Interlocked.Increment(ref summaryCalls) < 3 => throw new HttpRequestException("transient"),
            "/api/v2/summary/room/id/room-1" => AuthorizedSummary(request, "access-1"),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://agent.test/") };
        using var client = new Monze.HttpTranscriptClient(http, 123, "bot-token");

        var result = await client.FetchSummaryAsync("room-1", CancellationToken.None);

        Assert.Equal("đã xong", result?.Summary);
        Assert.Equal(3, summaryCalls);
    }

    [Fact]
    public void ExtractSummary_reads_participants_speech_durations_and_action_items()
    {
        const string json = """
            {
              "status":"ok",
              "data":{
                "room_id":"room-1",
                "created_at":"2026-10-01T10:00:00Z",
                "finalized_at":"2026-10-01T10:30:00Z",
                "participants":["100","200"],
                "speech_durations":[
                  {"participant_identity":"100","duration":900},
                  {"participant_identity":"200","duration":300}
                ],
                "summary_data":{
                  "summary":"đã xong",
                  "action_items":{"100":["Việc một","Việc hai"],"200":["Việc ba"]}
                },
                "full_text":"toàn bộ nội dung"
              }
            }
            """;

        var result = Monze.HttpTranscriptClient.ExtractSummary(json, "room-1");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Participants.Count);
        Assert.Equal(2, result.SpeechDurations.Count);
        Assert.Equal(2, result.ActionItems.Count);
        Assert.Equal("toàn bộ nội dung", result.FullText);
        Assert.Equal("2026-10-01T10:00:00.0000000+00:00", result.CreatedAt?.ToString("O"));
    }

    [Fact]
    [Req("REQ-TIME-001")]
    public async Task FetchSummary_reuses_the_access_token_until_the_injected_clock_nears_expiry()
    {
        var handler = new HttpTranscriptRecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/auth/mezon/bot/login" => Json(HttpStatusCode.OK, "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600}"),
            "/api/v2/auth/refresh" => Json(HttpStatusCode.OK, "{\"access_token\":\"access-2\",\"refresh_token\":\"refresh-2\",\"expires_in\":3600}"),
            "/api/v2/summary/room/id/room-1" => request.Headers.Authorization?.Parameter == "access-2"
                ? AuthorizedSummary(request, "access-2")
                : AuthorizedSummary(request, "access-1"),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://agent.test/") };
        using var client = new Monze.HttpTranscriptClient(http, 123, "bot-token", time);

        await client.FetchSummaryAsync("room-1", CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(3_569));
        await client.FetchSummaryAsync("room-1", CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count);

        time.Advance(TimeSpan.FromSeconds(1));
        var result = await client.FetchSummaryAsync("room-1", CancellationToken.None);

        Assert.Equal("đã xong", result?.Summary);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal("/api/v2/auth/refresh", handler.Requests[3].RequestUri!.AbsolutePath);
        Assert.Equal("access-2", handler.Requests[4].Headers.Authorization?.Parameter);
    }

    private static HttpResponseMessage AuthorizedSummary(HttpRequestMessage request, string expectedToken)
    {
        if (!string.Equals(request.Headers.Authorization?.Parameter, expectedToken, StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.Unauthorized, "{}");
        }

        return Json(
            HttpStatusCode.OK,
            "{\"status\":\"ok\",\"data\":{\"room_id\":\"room-1\",\"summary_data\":{\"summary\":\"đã xong\"},\"full_text\":\"không trả ra\"}}");
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
        => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

}
