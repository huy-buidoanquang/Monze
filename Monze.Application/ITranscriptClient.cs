namespace Monze.Application;

public interface ITranscriptClient
{
    Task<string?> FetchSummaryAsync(string roomId, CancellationToken cancellationToken);
}
