using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Monze.Simulator;

/// <summary>
/// How a <see cref="SimHttpHost"/> authenticates and answers: the bot id and
/// token the Agent SSE query and the transcript login must carry, the AI key
/// the completion request must carry as Bearer, the lifetime of issued
/// transcript access tokens, the SSE keepalive comment interval and the AI
/// answer.
/// </summary>
public sealed record SimHttpHostOptions
{
    public required long BotId { get; init; }

    public required string BotToken { get; init; }

    /// <summary>Bearer key /v1/chat/completions requires; null answers every completion with 401.</summary>
    public string? AiApiKey { get; init; }

    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>": keepalive" comment written to an idle healthy SSE stream; null sends none.</summary>
    public TimeSpan? SseKeepAlive { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The assistant content of a completion; the default echoes a short tag and the input length.</summary>
    public Func<SimAiRequest, string> AiReply { get; init; } = static request => $"Kết quả mô phỏng ({request.Input?.Length ?? 0} ký tự).";
}

/// <summary>
/// In-process HTTP fake (Kestrel on 127.0.0.1, random port) for every HTTP
/// dependency Monze has, read from the real clients:
/// <list type="bullet">
/// <item>Agent SSE (Mezon.Net.Sdk 1.6.2 src/Mezon.Net.Sdk/Agent/AgentSseManager.cs):
/// GET {base}/api/sse/metadata?appid={botId}&amp;token={token} with Accept
/// text/event-stream; frames are <c>event:</c> / <c>data:</c> lines and a
/// blank line, the event type comes from <c>event:</c> or the JSON
/// <c>event_type</c>. MezonClient maps room_started, room_ended and
/// room_summary_done. The SDK reconnects 3 s (+ up to 1 s jitter) after a
/// stream ends, backs off exponentially (cap 30 s) after failed opens, has no
/// read timeout and never sends Last-Event-ID. The fake writes
/// <c>event: connected</c> on open (as the live server does,
/// docs/test-artifacts/20261001-agent-sse-probe.json), an <c>id:</c> per
/// event and replays events after a Last-Event-ID it receives (resume
/// support of the real server is unverified).</item>
/// <item>Transcript (Infrastructure/Agent/HttpTranscriptClient.cs): POST
/// api/v2/auth/mezon/bot/login with {"account":{"appid","token"}}, POST
/// api/v2/auth/refresh with {"refresh_token"}, both answering
/// {access_token, refresh_token, expires_in}; GET
/// api/v2/summary/room/id/{roomId} with a Bearer access token, answering the
/// stored summary JSON or 404 while none is stored.</item>
/// <item>AI (Features/Ai/OpenAiCompatibleProvider.cs): POST v1/chat/completions
/// with Bearer {Monze:Ai:ApiKey}, {model, max_tokens, temperature, messages:
/// [system, user]}, answering {choices:[{message:{content}}]}.</item>
/// </list>
/// Every request is recorded without its query string. Unknown paths answer
/// 404 and are recorded in <see cref="Unmodelled"/>. Scripted faults:
/// <see cref="Faults"/> (delay, status, hang, empty, oversized, malformed and slow-drip per
/// route) and the SSE stream controls (duplicate, held/reordered and raw
/// frames, dropped and half-open streams).
/// </summary>
public sealed class SimHttpHost : IAsyncDisposable
{
    public const string SsePath = "/api/sse/metadata";
    public const string LoginPath = "/api/v2/auth/mezon/bot/login";
    public const string RefreshPath = "/api/v2/auth/refresh";
    public const string SummaryPathPrefix = "/api/v2/summary/room/id/";
    public const string CompletionsPath = "/v1/chat/completions";

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly List<SimHttpRequest> _requests = [];
    private readonly List<string> _unmodelled = [];
    private readonly List<SimAiRequest> _aiRequests = [];
    private readonly Dictionary<string, string> _summaries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _accessTokens = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refreshTokens = new(StringComparer.Ordinal);
    private readonly List<SseStream> _streams = [];
    private readonly List<(long Id, string Frame)> _history = [];
    private readonly List<string> _held = [];
    private long _sequence;
    private long _eventId;
    private long _completions;
    private int _tokens;
    private int _sseAccepted;
    private bool _disposed;

    private SimHttpHost(WebApplication app, SimHttpHostOptions options)
    {
        _app = app;
        Options = options;
    }

    public SimHttpHostOptions Options { get; }

    /// <summary>Scripted faults per route.</summary>
    public SimHttpFaultPlan Faults { get; } = new();

    /// <summary>http://127.0.0.1:{port}, the value for Mezon:AgentBaseUrl and Monze:Ai:BaseUrl.</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    public IReadOnlyList<SimHttpRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>"METHOD path" of every request to a path the fake does not model (answered 404).</summary>
    public IReadOnlyList<string> Unmodelled
    {
        get
        {
            lock (_gate)
            {
                return _unmodelled.ToList();
            }
        }
    }

    public IReadOnlyList<SimAiRequest> AiRequests
    {
        get
        {
            lock (_gate)
            {
                return _aiRequests.ToList();
            }
        }
    }

    /// <summary>SSE streams accepted with 200 so far (reconnects included).</summary>
    public int SseConnections => Volatile.Read(ref _sseAccepted);

    /// <summary>SSE streams currently open and delivering (half-open ones excluded).</summary>
    public int OpenSseStreams
    {
        get
        {
            lock (_gate)
            {
                return _streams.Count(static stream => !stream.Hung);
            }
        }
    }

    public static async Task<SimHttpHost> StartAsync(SimHttpHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ApplicationName = "Monze.Simulator.Http" });
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0);
            kestrel.AddServerHeader = false;
        });
        var app = builder.Build();
        var host = new SimHttpHost(app, options);
        app.Run(host.HandleAsync);
        await app.StartAsync().ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        host.BaseUrl = address.Replace("[::1]", "127.0.0.1", StringComparison.Ordinal).TrimEnd('/');
        return host;
    }

    /// <summary>Stores the JSON the summary endpoint returns for <paramref name="roomId"/>.</summary>
    public void SetSummary(string roomId, string json)
    {
        lock (_gate)
        {
            _summaries[roomId] = json;
        }
    }

    /// <summary>
    /// The summary JSON shape AgentSummaryParser reads: room_id,
    /// summary_data.summary and action_items, created/finalized time,
    /// participants and speech durations.
    /// </summary>
    public static string SummaryJson(
        string roomId,
        string summary,
        IReadOnlyDictionary<string, string[]>? actionItems = null,
        IReadOnlyList<(string Identity, double Seconds)>? speakers = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? finalizedAt = null)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["room_id"] = roomId,
            ["created_at"] = (createdAt ?? DateTimeOffset.UtcNow.AddMinutes(-30)).ToString("O", CultureInfo.InvariantCulture),
            ["finalized_at"] = (finalizedAt ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture),
            ["participants"] = (speakers ?? []).Select(static speaker => speaker.Identity).ToArray(),
            ["speech_durations"] = (speakers ?? []).Select(static speaker => new Dictionary<string, object>
            {
                ["participant_identity"] = speaker.Identity,
                ["duration"] = speaker.Seconds
            }).ToArray(),
            ["full_text"] = "Bản ghi mô phỏng.",
            ["summary_data"] = new Dictionary<string, object?>
            {
                ["summary"] = summary,
                ["action_items"] = actionItems ?? new Dictionary<string, string[]>()
            }
        }, Json);

    /// <summary>Invalidates every issued transcript access token (the next summary call gets 401).</summary>
    public void RevokeAccessTokens()
    {
        lock (_gate)
        {
            _accessTokens.Clear();
        }
    }

    /// <summary>
    /// Publishes an Agent event to every open, healthy SSE stream and returns
    /// once it was flushed; returns the number of streams that got it (0 means
    /// the event is lost unless a client resumes with Last-Event-ID).
    /// </summary>
    public Task<int> PublishAgentEventAsync(
        string eventType,
        string roomId,
        long? voiceChannelId = null,
        long? clanId = null,
        string? eventId = null,
        SimSseDelivery delivery = SimSseDelivery.Normal,
        bool eventField = true)
    {
        var payload = new Dictionary<string, object?>
        {
            ["event_type"] = eventType,
            ["event_id"] = eventId ?? $"sim-{eventType}-{roomId}-{Guid.NewGuid():N}",
            ["room_id"] = roomId,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        if (voiceChannelId is long voice)
        {
            payload["voice_channel_id"] = voice.ToString(CultureInfo.InvariantCulture);
        }

        if (clanId is long clan)
        {
            payload["clan_id"] = clan.ToString(CultureInfo.InvariantCulture);
        }

        return PublishJsonAsync(eventField ? eventType : null, JsonSerializer.Serialize(payload, Json), delivery);
    }

    /// <summary>Publishes <paramref name="json"/> as one SSE event (with an <c>event:</c> line unless null).</summary>
    public Task<int> PublishJsonAsync(string? eventType, string json, SimSseDelivery delivery = SimSseDelivery.Normal)
    {
        var id = Interlocked.Increment(ref _eventId);
        var frame = new StringBuilder();
        frame.Append("id: ").Append(id.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (eventType is not null)
        {
            frame.Append("event: ").Append(eventType).Append('\n');
        }

        foreach (var line in json.Split('\n'))
        {
            frame.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
        }

        frame.Append('\n');
        return PublishFrameAsync(id, frame.ToString(), delivery);
    }

    /// <summary>Writes raw text to every open stream (malformed frames, comments, empty events).</summary>
    public Task<int> PublishRawAsync(string text) => PublishFrameAsync(null, text, SimSseDelivery.Normal);

    /// <summary>Writes frames held by <see cref="SimSseDelivery.Hold"/> without waiting for the next event.</summary>
    public async Task<int> ReleaseHeldEventsAsync()
    {
        string[] held;
        lock (_gate)
        {
            held = _held.ToArray();
            _held.Clear();
        }

        var written = 0;
        foreach (var frame in held)
        {
            written += await WriteToStreamsAsync([frame]).ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>The server ends every open SSE response (the SDK reconnects about 3 s later).</summary>
    public int DropSseStreams()
    {
        lock (_gate)
        {
            foreach (var stream in _streams)
            {
                stream.Frames.Writer.TryComplete();
            }

            return _streams.Count;
        }
    }

    /// <summary>
    /// Every open stream goes half-open: the connection stays up but nothing
    /// reaches the client any more, keepalives included (a silent network
    /// partition). Returns the number of streams affected.
    /// </summary>
    public int HangSseStreams()
    {
        lock (_gate)
        {
            foreach (var stream in _streams)
            {
                stream.Hung = true;
            }

            return _streams.Count;
        }
    }

    /// <summary>Waits until at least <paramref name="connections"/> streams were accepted and one is open.</summary>
    public async Task WaitForSseAsync(int connections, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (SseConnections < connections || OpenSseStreams == 0)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Expected {connections} Agent SSE connection(s); saw {SseConnections}, {OpenSseStreams} open.{Environment.NewLine}{Describe()}");
            }

            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until <paramref name="count"/> recorded requests match.</summary>
    public async Task<IReadOnlyList<SimHttpRequest>> WaitForRequestsAsync(Func<SimHttpRequest, bool> predicate, int count, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var matches = Requests.Where(predicate).ToList();
            if (matches.Count >= count)
            {
                return matches;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Expected {count} matching HTTP request(s), saw {matches.Count}.{Environment.NewLine}{Describe()}");
            }

            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    /// <summary>The last <paramref name="last"/> requests, one per line, for failure messages.</summary>
    public string Describe(int last = 40)
    {
        var requests = Requests;
        var text = new StringBuilder($"HTTP fake: {requests.Count} request(s), {SseConnections} SSE connection(s), {OpenSseStreams} open, {Unmodelled.Count} unmodelled.");
        foreach (var request in requests.TakeLast(last))
        {
            text.AppendLine().Append(CultureInfo.InvariantCulture, $"  #{request.Sequence} {request.At:HH:mm:ss.fff} {request.Method} {request.Path} -> {request.Status}{(request.Fault is { } fault ? $" [{fault}]" : string.Empty)}{(request.Detail is { } detail ? $" {detail}" : string.Empty)}");
        }

        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stopping.CancelAsync().ConfigureAwait(false);
        DropSseStreams();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _app.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task<int> PublishFrameAsync(long? id, string frame, SimSseDelivery delivery)
    {
        string[] frames;
        lock (_gate)
        {
            if (id is long eventId)
            {
                _history.Add((eventId, frame));
            }

            if (delivery == SimSseDelivery.Hold)
            {
                _held.Add(frame);
                return 0;
            }

            var list = new List<string> { frame };
            if (delivery == SimSseDelivery.Duplicate)
            {
                list.Add(frame);
            }

            list.AddRange(_held);
            _held.Clear();
            frames = list.ToArray();
        }

        return await WriteToStreamsAsync(frames).ConfigureAwait(false);
    }

    private async Task<int> WriteToStreamsAsync(IReadOnlyList<string> frames)
    {
        List<(SseStream Stream, TaskCompletionSource Done)> targets;
        lock (_gate)
        {
            targets = _streams.Where(static stream => !stream.Hung)
                .Select(static stream => (stream, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)))
                .ToList();
        }

        var written = 0;
        foreach (var (stream, done) in targets)
        {
            if (stream.Frames.Writer.TryWrite(new SseFrame(string.Concat(frames), done)))
            {
                written++;
            }
            else
            {
                done.TrySetResult();
            }
        }

        foreach (var (_, done) in targets)
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        return written;
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path.Value ?? string.Empty;
        SimHttpRoute? route = (request.Method, path) switch
        {
            ("GET", SsePath) => SimHttpRoute.AgentSse,
            ("POST", LoginPath) => SimHttpRoute.TranscriptLogin,
            ("POST", RefreshPath) => SimHttpRoute.TranscriptRefresh,
            ("POST", CompletionsPath) => SimHttpRoute.AiCompletion,
            ("GET", _) when path.StartsWith(SummaryPathPrefix, StringComparison.Ordinal) && path.Length > SummaryPathPrefix.Length => SimHttpRoute.TranscriptSummary,
            _ => null
        };
        if (route is not SimHttpRoute known)
        {
            lock (_gate)
            {
                _unmodelled.Add($"{request.Method} {path}");
            }

            Record(null, request.Method, path, 404, null, "unmodelled");
            context.Response.StatusCode = 404;
            return;
        }

        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping.Token);
        var token = aborted.Token;
        var (delay, terminal) = Faults.Take(known);
        try
        {
            if (delay is not null)
            {
                await Task.Delay(delay.Delay, token).ConfigureAwait(false);
            }

            if (terminal?.Kind == SimHttpFaultKind.Hang)
            {
                Record(known, request.Method, path, 0, SimHttpFaultKind.Hang, "never answered");
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return;
            }

            if (terminal?.Kind == SimHttpFaultKind.Status)
            {
                Record(known, request.Method, path, terminal.StatusCode, SimHttpFaultKind.Status);
                await WriteJsonAsync(context, terminal.StatusCode, "{\"error\":\"simulated failure\"}", token).ConfigureAwait(false);
                return;
            }

            switch (known)
            {
                case SimHttpRoute.AgentSse:
                    await ServeSseAsync(context, path, terminal, token).ConfigureAwait(false);
                    break;
                case SimHttpRoute.TranscriptLogin:
                case SimHttpRoute.TranscriptRefresh:
                    await ServeTokenAsync(context, known, path, terminal, token).ConfigureAwait(false);
                    break;
                case SimHttpRoute.TranscriptSummary:
                    await ServeSummaryAsync(context, path, terminal, token).ConfigureAwait(false);
                    break;
                case SimHttpRoute.AiCompletion:
                    await ServeCompletionAsync(context, path, terminal, token).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // The client went away while the fake was writing.
        }
    }

    private async Task ServeSseAsync(HttpContext context, string path, SimHttpFault? terminal, CancellationToken token)
    {
        var query = context.Request.Query;
        var lastEventId = context.Request.Headers["Last-Event-ID"].ToString();
        var hadLastEventId = !string.IsNullOrEmpty(lastEventId);
        if (query["appid"].ToString() != Options.BotId.ToString(CultureInfo.InvariantCulture)
            || query["token"].ToString() != Options.BotToken)
        {
            Record(SimHttpRoute.AgentSse, "GET", path, 401, null, "bad appid or token", hadLastEventId);
            context.Response.StatusCode = 401;
            return;
        }

        Record(SimHttpRoute.AgentSse, "GET", path, 200, terminal?.Kind, null, hadLastEventId);
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.StartAsync(token).ConfigureAwait(false);
        if (terminal?.Kind == SimHttpFaultKind.Empty)
        {
            return;
        }

        var stream = new SseStream();
        await WriteAsync(context, "event: connected\ndata: {\"connection_id\":\"sim\",\"channel\":\"metadata\",\"message\":\"connected\"}\n\n", token).ConfigureAwait(false);
        if (terminal?.Kind == SimHttpFaultKind.Oversized)
        {
            await WriteAsync(context, $"event: room_started\ndata: {{\"event_type\":\"room_started\",\"pad\":\"{new string('x', terminal.Bytes)}\"}}\n\n", token).ConfigureAwait(false);
        }
        else if (terminal?.Kind == SimHttpFaultKind.Malformed)
        {
            await WriteAsync(context, "data: {not json\n\nthis line has no field name\nevent: room_started\ndata: [1,2\n\n", token).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (hadLastEventId && long.TryParse(lastEventId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resumeAfter))
            {
                foreach (var (id, frame) in _history.Where(entry => entry.Id > resumeAfter))
                {
                    stream.Frames.Writer.TryWrite(new SseFrame(frame, null));
                }
            }

            _streams.Add(stream);
        }

        Interlocked.Increment(ref _sseAccepted);
        try
        {
            var reader = stream.Frames.Reader;
            while (!token.IsCancellationRequested)
            {
                var keepAlive = Options.SseKeepAlive ?? Timeout.InfiniteTimeSpan;
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(keepAlive);
                bool more;
                try
                {
                    more = await reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    if (!stream.Hung)
                    {
                        await WriteAsync(context, ": keepalive\n\n", token).ConfigureAwait(false);
                    }

                    continue;
                }

                if (!more)
                {
                    return;
                }

                while (reader.TryRead(out var frame))
                {
                    if (!stream.Hung)
                    {
                        await WriteAsync(context, frame.Text, token).ConfigureAwait(false);
                    }

                    frame.Done?.TrySetResult();
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _streams.Remove(stream);
            }

            stream.Frames.Writer.TryComplete();
            while (stream.Frames.Reader.TryRead(out var pending))
            {
                pending.Done?.TrySetResult();
            }
        }
    }

    private async Task ServeTokenAsync(HttpContext context, SimHttpRoute route, string path, SimHttpFault? terminal, CancellationToken token)
    {
        var body = await ReadBodyAsync(context, token).ConfigureAwait(false);
        var valid = false;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (route == SimHttpRoute.TranscriptLogin)
            {
                valid = root.TryGetProperty("account", out var account)
                    && account.TryGetProperty("appid", out var appId)
                    && appId.ValueKind == JsonValueKind.String
                    && appId.GetString() == Options.BotId.ToString(CultureInfo.InvariantCulture)
                    && account.TryGetProperty("token", out var botToken)
                    && botToken.GetString() == Options.BotToken;
            }
            else if (root.TryGetProperty("refresh_token", out var refresh) && refresh.GetString() is { } refreshToken)
            {
                lock (_gate)
                {
                    valid = _refreshTokens.Remove(refreshToken);
                }
            }
        }
        catch (JsonException)
        {
        }

        if (!valid)
        {
            Record(route, "POST", path, 401, null, "bad credentials");
            await WriteJsonAsync(context, 401, "{\"error\":\"unauthorized\"}", token).ConfigureAwait(false);
            return;
        }

        if (await TryWriteBodyFaultAsync(context, route, path, terminal, token).ConfigureAwait(false))
        {
            return;
        }

        var number = Interlocked.Increment(ref _tokens);
        var access = $"sim-access-{number}-{Guid.NewGuid():N}";
        var refreshNext = $"sim-refresh-{number}-{Guid.NewGuid():N}";
        lock (_gate)
        {
            _accessTokens[access] = DateTimeOffset.UtcNow + Options.AccessTokenLifetime;
            _refreshTokens.Add(refreshNext);
        }

        Record(route, "POST", path, 200, null);
        await WriteJsonAsync(
            context,
            200,
            JsonSerializer.Serialize(new { access_token = access, refresh_token = refreshNext, expires_in = (long)Options.AccessTokenLifetime.TotalSeconds }),
            token).ConfigureAwait(false);
    }

    private async Task ServeSummaryAsync(HttpContext context, string path, SimHttpFault? terminal, CancellationToken token)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization["Bearer ".Length..] : string.Empty;
        bool authorized;
        lock (_gate)
        {
            authorized = _accessTokens.TryGetValue(bearer, out var expires) && expires > DateTimeOffset.UtcNow;
        }

        if (!authorized)
        {
            Record(SimHttpRoute.TranscriptSummary, "GET", path, 401, null, "bad bearer");
            await WriteJsonAsync(context, 401, "{\"error\":\"unauthorized\"}", token).ConfigureAwait(false);
            return;
        }

        if (await TryWriteBodyFaultAsync(context, SimHttpRoute.TranscriptSummary, path, terminal, token).ConfigureAwait(false))
        {
            return;
        }

        var roomId = Uri.UnescapeDataString(path[SummaryPathPrefix.Length..]);
        string? summary;
        lock (_gate)
        {
            _summaries.TryGetValue(roomId, out summary);
        }

        if (summary is null)
        {
            Record(SimHttpRoute.TranscriptSummary, "GET", path, 404, null, "no summary stored");
            await WriteJsonAsync(context, 404, "{\"error\":\"not found\"}", token).ConfigureAwait(false);
            return;
        }

        if (terminal?.Kind == SimHttpFaultKind.Drip)
        {
            Record(SimHttpRoute.TranscriptSummary, "GET", path, 200, SimHttpFaultKind.Drip);
            await DripJsonAsync(context, summary, terminal.Delay, token).ConfigureAwait(false);
            return;
        }

        Record(SimHttpRoute.TranscriptSummary, "GET", path, 200, null);
        await WriteJsonAsync(context, 200, summary, token).ConfigureAwait(false);
    }

    private async Task ServeCompletionAsync(HttpContext context, string path, SimHttpFault? terminal, CancellationToken token)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (Options.AiApiKey is null || authorization != "Bearer " + Options.AiApiKey)
        {
            Record(SimHttpRoute.AiCompletion, "POST", path, 401, null, "bad api key");
            await WriteJsonAsync(context, 401, "{\"error\":\"unauthorized\"}", token).ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(context, token).ConfigureAwait(false);
        SimAiRequest parsed;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var messages = root.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().ToList()
                : [];
            string? Content(string role)
                => messages.FirstOrDefault(message => message.TryGetProperty("role", out var value) && value.GetString() == role) is { ValueKind: JsonValueKind.Object } message
                    && message.TryGetProperty("content", out var content)
                    ? content.GetString()
                    : null;
            parsed = new SimAiRequest(
                root.TryGetProperty("model", out var model) ? model.GetString() : null,
                Content("system"),
                Content("user"),
                root.TryGetProperty("max_tokens", out var maxTokens) && maxTokens.TryGetInt32(out var max) ? max : null);
        }
        catch (JsonException)
        {
            Record(SimHttpRoute.AiCompletion, "POST", path, 400, null, "request is not JSON");
            await WriteJsonAsync(context, 400, "{\"error\":\"bad request\"}", token).ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            _aiRequests.Add(parsed);
        }

        if (await TryWriteBodyFaultAsync(context, SimHttpRoute.AiCompletion, path, terminal, token).ConfigureAwait(false))
        {
            return;
        }

        Record(SimHttpRoute.AiCompletion, "POST", path, 200, terminal?.Kind == SimHttpFaultKind.Drip ? SimHttpFaultKind.Drip : null);
        var answer = new
        {
            id = $"sim-completion-{Interlocked.Increment(ref _completions)}",
            @object = "chat.completion",
            model = parsed.Model,
            choices = new[] { new { index = 0, message = new { role = "assistant", content = Options.AiReply(parsed) }, finish_reason = "stop" } }
        };
        if (terminal?.Kind == SimHttpFaultKind.Drip)
        {
            await DripJsonAsync(context, JsonSerializer.Serialize(answer, Json), terminal.Delay, token).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(context, 200, JsonSerializer.Serialize(answer, Json), token).ConfigureAwait(false);
    }

    private async Task<bool> TryWriteBodyFaultAsync(HttpContext context, SimHttpRoute route, string path, SimHttpFault? terminal, CancellationToken token)
    {
        switch (terminal?.Kind)
        {
            case SimHttpFaultKind.Empty:
                Record(route, context.Request.Method, path, 200, SimHttpFaultKind.Empty);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength = 0;
                return true;
            case SimHttpFaultKind.Malformed:
                Record(route, context.Request.Method, path, 200, SimHttpFaultKind.Malformed);
                await WriteJsonAsync(context, 200, "{\"choices\": [ {\"message\": ", token).ConfigureAwait(false);
                return true;
            case SimHttpFaultKind.Oversized:
                Record(route, context.Request.Method, path, 200, SimHttpFaultKind.Oversized, $"{terminal.Bytes} bytes");
                await WriteJsonAsync(context, 200, "{\"pad\":\"" + new string('x', Math.Max(0, terminal.Bytes - 10)) + "\"}", token).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Writes <paramref name="json"/> one byte per <see cref="SimHttpFault.Delay"/> after sending the headers.</summary>
    private static async Task DripJsonAsync(HttpContext context, string json, TimeSpan perByte, CancellationToken token)
    {
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentLength = bytes.Length;
        await context.Response.StartAsync(token).ConfigureAwait(false);
        for (var i = 0; i < bytes.Length; i++)
        {
            await context.Response.Body.WriteAsync(bytes.AsMemory(i, 1), token).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
            await Task.Delay(perByte, token).ConfigureAwait(false);
        }
    }

    private void Record(SimHttpRoute? route, string method, string path, int status, SimHttpFaultKind? fault, string? detail = null, bool hadLastEventId = false)
    {
        lock (_gate)
        {
            _requests.Add(new SimHttpRequest(++_sequence, DateTimeOffset.UtcNow, route, method, path, status, fault, detail, hadLastEventId));
        }
    }

    private static async Task<string> ReadBodyAsync(HttpContext context, CancellationToken token)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(HttpContext context, int status, string json, CancellationToken token)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpContext context, string text, CancellationToken token)
    {
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), token).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
    }

    private sealed record SseFrame(string Text, TaskCompletionSource? Done);

    private sealed class SseStream
    {
        private int _hung;

        public Channel<SseFrame> Frames { get; } = Channel.CreateUnbounded<SseFrame>(new UnboundedChannelOptions { SingleReader = true });

        public bool Hung
        {
            get => Volatile.Read(ref _hung) == 1;
            set => Volatile.Write(ref _hung, value ? 1 : 0);
        }
    }
}
