using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Hosting;

namespace Monze;

public sealed class MeetingMaintenanceWorker(
    IMeetingRepository meeting,
    ICommandInboxRepository commandInbox,
    ITranscriptClient transcript,
    ILogger<MeetingMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartupSchemaValidator.Ready.WaitAsync(stoppingToken);
        var nextInboxPurge = DateTimeOffset.UtcNow;
        var nextCommandInboxPurge = nextInboxPurge;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                await meeting.ExpireSuggestedAsync(now, stoppingToken);
                await RetryPendingSummariesAsync(meeting, transcript, logger, stoppingToken);
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

    private static async Task RetryPendingSummariesAsync(
        IMeetingRepository meeting,
        ITranscriptClient transcript,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var pending = await meeting.ListPendingSummariesAsync(32, cancellationToken);
        for (var i = 0; i < pending.Count; i++)
        {
            var item = pending[i];
            try
            {
                var summary = await transcript.FetchSummaryAsync(item.RoomId, cancellationToken);
                if (string.IsNullOrWhiteSpace(summary))
                {
                    await meeting.RecordSummaryRetryAsync(
                        item.RoomId,
                        "summary-empty",
                        cancellationToken,
                        item.LeaseToken);
                    continue;
                }

                await meeting.StoreSummaryAsync(
                    item.RoomId,
                    summary,
                    null,
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
