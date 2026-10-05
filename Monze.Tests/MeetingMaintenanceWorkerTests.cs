using Microsoft.Extensions.Logging.Abstractions;
using Monze.Application;
using Xunit;

namespace Monze.Tests;

public sealed class MeetingMaintenanceWorkerTests
{
    [Fact]
    public async Task RetryPendingSummaries_claims_each_lease_immediately_before_processing()
    {
        var repository = new MonzeAppTestDependencies();
        repository.PendingMeetingSummaries.Enqueue(new PendingMeetingSummary("room-1", 0, "lease-1"));
        repository.PendingMeetingSummaries.Enqueue(new PendingMeetingSummary("room-2", 0, "lease-2"));
        var composer = new MeetingSummaryComposer(new StubMeetingUserProfileRepository());

        await MeetingMaintenanceWorker.RetryPendingSummariesAsync(
            repository,
            new DisabledTranscriptClient(),
            composer,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.All(repository.PendingSummaryClaimLimits, limit => Assert.Equal(1, limit));
        Assert.Equal(2, repository.SummaryRetries.Count);
        Assert.Contains(("room-1", "summary-empty", "lease-1"), repository.SummaryRetries);
        Assert.Contains(("room-2", "summary-empty", "lease-2"), repository.SummaryRetries);
    }

    [Fact]
    public async Task RetryPendingSummaries_limits_concurrency_and_total_batch_size()
    {
        var repository = new MonzeAppTestDependencies();
        for (var i = 1; i <= 33; i++)
        {
            repository.PendingMeetingSummaries.Enqueue(
                new PendingMeetingSummary($"room-{i}", 0, $"lease-{i}"));
        }

        var transcript = new MeetingMaintenanceTranscriptClient(TimeSpan.FromMilliseconds(10));
        var composer = new MeetingSummaryComposer(new StubMeetingUserProfileRepository());

        await MeetingMaintenanceWorker.RetryPendingSummariesAsync(
            repository,
            transcript,
            composer,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(4, transcript.MaxConcurrency);
        Assert.Equal(32, repository.SummaryRetries.Count);
        Assert.Single(repository.PendingMeetingSummaries);
        Assert.All(repository.PendingSummaryClaimLimits, limit => Assert.Equal(1, limit));
    }
}
