using Monze.Application;

namespace Monze;

public sealed class DisabledTranscriptClient : ITranscriptClient
{
    public Task<string?> FetchSummaryAsync(string roomId, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}
