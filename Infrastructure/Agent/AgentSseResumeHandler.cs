using Microsoft.Extensions.Logging;

namespace Monze;

/// <summary>
/// The HTTP pipeline under the Agent SSE stream (DEF-08: SDK 1.6.2
/// AgentSseManager reads with no idle timeout and never sends
/// Last-Event-ID). It resumes after the last event seen and wraps every
/// event-stream body in an <see cref="AgentSseIdleStream"/>.
/// </summary>
internal sealed class AgentSseResumeHandler(AgentSseResumeState state, TimeProvider time, ILogger logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (state.LastEventId is { } lastEventId)
        {
            request.Headers.Remove("Last-Event-ID");
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            return response;
        }

        var original = response.Content;
        var body = await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var wrapped = new StreamContent(new AgentSseIdleStream(body, state, time, logger, original));
        foreach (var header in original.Headers)
        {
            wrapped.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = wrapped;
        return response;
    }
}
