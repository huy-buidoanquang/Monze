namespace Monze.Hosting;

/// <summary>
/// Signals that the startup schema validation finished. One instance per
/// host, so two hosts in one process (tests) do not share readiness.
/// </summary>
public sealed class StartupReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => _ready.Task;

    public void MarkReady() => _ready.TrySetResult();

    public void MarkFailed(Exception exception) => _ready.TrySetException(exception);
}
