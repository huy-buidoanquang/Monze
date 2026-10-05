namespace Monze.Tests;

internal sealed class OpenAiRecordingHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public OpenAiRecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : this((request, _) => responder(request))
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => responder(request, cancellationToken);
}
