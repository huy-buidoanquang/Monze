namespace Monze.Application;

public interface IAiProvider
{
    Task<string?> CompleteAsync(string instruction, string input, CancellationToken cancellationToken);
}
