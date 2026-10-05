using Monze.Application;

namespace Monze;

public sealed class DisabledTranscriptClient : ITranscriptClient
{
    public Task<AgentSummaryResult?> FetchSummaryAsync(string roomId, CancellationToken cancellationToken)
        => Task.FromResult<AgentSummaryResult?>(null);
}
