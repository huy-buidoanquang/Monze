namespace Monze.Tests;

internal sealed class HttpTranscriptTimeoutHandler : HttpMessageHandler
{
    private int _requestCount;

    public int RequestCount => Volatile.Read(ref _requestCount);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The infinite delay must be cancelled.");
    }
}
