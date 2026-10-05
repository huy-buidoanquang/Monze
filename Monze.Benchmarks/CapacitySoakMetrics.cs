internal sealed class CapacitySoakMetrics
{
    public string Scope { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public double DurationSeconds { get; init; }
    public double ElapsedSeconds { get; set; }
    public int ClanCount { get; init; }
    public int ActiveClanCount { get; init; }
    public int MessageRate { get; init; }
    public int CommandRate { get; init; }
    public int MeetingCandidateCount { get; init; }
    public int OutboxRate { get; init; }
    public int QueueCapacity { get; init; }
    public int PartitionCount { get; init; }
    public long MessagesProduced { get; set; }
    public long MessageDrops { get; set; }
    public long CommandsProduced { get; set; }
    public long CommandRejects { get; set; }
    public long OutboxProduced { get; set; }
    public long OutboxDrops { get; set; }
    public int MeetingCandidatesObserved { get; set; }
    public int MaxIngressDepth { get; set; }
    public int MaxOutboxDepth { get; set; }
    public long ManagedHeapBytes { get; set; }
    public long WorkingSetBytes { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public int Samples { get; set; }
}
