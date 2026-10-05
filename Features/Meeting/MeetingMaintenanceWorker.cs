using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Hosting;

namespace Monze;

public sealed class MeetingMaintenanceWorker(
    IMeetingRepository meeting,
    ICommandInboxRepository commandInbox,
    IInteractionInboxRepository interactionInbox,
    ITranscriptClient transcript,
    MeetingSummaryComposer summaryComposer,
    ILogger<MeetingMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartupSchemaValidator.Ready.WaitAsync(stoppingToken);
        var nextInboxPurge = DateTimeOffset.UtcNow;
        var nextCommandInboxPurge = nextInboxPurge;
        var nextInteractionInboxPurge = nextInboxPurge;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                await meeting.ExpireSuggestedAsync(now, stoppingToken);
                await RetryPendingSummariesAsync(meeting, transcript, summaryComposer, logger, stoppingToken);
                if (now >= nextInboxPurge)
                {
                    await meeting.PurgeInboxAsync(now.AddDays(-30), stoppingToken);
                    nextInboxPurge = now.AddHours(1);
                }

                if (now >= nextCommandInboxPurge)
                {
                    await commandInbox.PurgeAsync(now.AddDays(-30), stoppingToken);
                    nextCommandInboxPurge = now.AddHours(1);
                }

                if (now >= nextInteractionInboxPurge)
                {
                    await interactionInbox.PurgeAsync(now.AddDays(-30), stoppingToken);
                    nextInteractionInboxPurge = now.AddHours(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Meeting maintenance iteration failed; retrying.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    internal static async Task RetryPendingSummariesAsync(
        IMeetingRepository meeting,
        ITranscriptClient transcript,
        MeetingSummaryComposer summaryComposer,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        const int batchLimit = 32;
        const int concurrency = 4;
        var claimSlots = 0;
        var workers = new Task[concurrency];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = RunLaneAsync();
        }

        await Task.WhenAll(workers);

        async Task RunLaneAsync()
        {
            while (Interlocked.Increment(ref claimSlots) <= batchLimit)
            {
                var pending = await meeting.ListPendingSummariesAsync(1, cancellationToken);
                if (pending.Count == 0)
                {
                    return;
                }

                var item = pending[0];
                try
                {
                    var summary = await transcript.FetchSummaryAsync(item.RoomId, cancellationToken);
                    if (summary is null || string.IsNullOrWhiteSpace(summary.Summary))
                    {
                        await meeting.RecordSummaryRetryAsync(
                            item.RoomId,
                            "summary-empty",
                            cancellationToken,
                            item.LeaseToken);
                        continue;
                    }

                    var context = await meeting.GetSummaryContextAsync(item.RoomId, cancellationToken);
                    var delivery = context is null
                        ? null
                        : await summaryComposer.ComposeAsync(summary, context, cancellationToken);
                    await meeting.StoreSummaryAsync(
                        item.RoomId,
                        summary.Summary,
                        summary.FullTranscriptJson,
                        delivery,
                        cancellationToken,
                        item.LeaseToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Pending meeting summary retry failed for room {RoomId}.", item.RoomId);
                    await meeting.RecordSummaryRetryAsync(
                        item.RoomId,
                        ex.GetType().Name,
                        cancellationToken,
                        item.LeaseToken);
                }
            }
        }
    }
}
