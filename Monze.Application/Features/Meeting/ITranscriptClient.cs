namespace Monze.Application;

public interface ITranscriptClient
{
    Task<AgentSummaryResult?> FetchSummaryAsync(string roomId, CancellationToken cancellationToken);
}
