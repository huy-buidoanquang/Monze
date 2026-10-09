namespace Monze.Campaign.Load;

/// <summary>
/// Counts failures of fire-and-forget load pushes (and remembers the first
/// few exception types), so a failing generator is visible in the artifact.
/// </summary>
public sealed class LoadErrors
{
    private readonly Lock _gate = new();
    private readonly List<string> _types = [];
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public IReadOnlyList<string> Types
    {
        get
        {
            lock (_gate)
            {
                return _types.ToList();
            }
        }
    }

    public void Watch(Task task)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = task.ContinueWith(
            static (completed, state) =>
            {
                if (completed.Exception is { } error)
                {
                    ((LoadErrors)state!).Add(error.InnerException ?? error);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Add(Exception error)
    {
        Interlocked.Increment(ref _count);
        lock (_gate)
        {
            if (_types.Count < 5 && !_types.Contains(error.GetType().Name))
            {
                _types.Add(error.GetType().Name);
            }
        }
    }
}
