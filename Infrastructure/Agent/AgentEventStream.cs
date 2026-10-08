using Mezon.Net.Sdk.Agent;
using Microsoft.Extensions.Logging;

namespace Monze;

/// <summary>
/// Monze's Agent SSE connection: the SDK's AgentSseManager (same endpoint,
/// decoder and reconnect backoff as MezonClient.ConnectAgentSseAsync) on an
/// HttpClient Monze owns, whose <see cref="AgentSseResumeHandler"/> resumes
/// with Last-Event-ID and ends a half-open stream (DEF-08).
/// </summary>
internal sealed class AgentEventStream : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly AgentSseManager _manager;

    public AgentEventStream(
        string baseUrl,
        long botId,
        string token,
        TimeSpan idleTimeout,
        TimeProvider time,
        ILogger logger,
        Func<AgentSseSessionEvent, Task> onEvent)
    {
        var state = new AgentSseResumeState(idleTimeout);
        // An ended stream's connection is closed at once instead of being
        // drained for reuse (a half-open one would only time out the drain).
        var transport = new SocketsHttpHandler { ResponseDrainTimeout = TimeSpan.Zero, MaxResponseDrainSize = 0 };
        _http = new HttpClient(new AgentSseResumeHandler(state, time, logger) { InnerHandler = transport })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _manager = new AgentSseManager(baseUrl, botId, token, _http, maxReconnectAttempts: 0);
        _manager.MessageReceived += onEvent;
    }

    public Task ConnectAsync(CancellationToken cancellationToken) => _manager.ConnectAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        _http.Dispose();
    }
}
