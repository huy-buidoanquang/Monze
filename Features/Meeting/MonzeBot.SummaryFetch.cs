using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Monze;

public sealed partial class MonzeBot
{
    /// <summary>
    /// room_summary_done events whose transcript is still to be fetched. The
    /// fetch runs on its own worker, not in the Agent ingress worker, so a slow
    /// or hung transcript service no longer holds every Agent and voice event
    /// behind it (WF-04). When the queue is full the session is left
    /// summary_pending for the meeting maintenance worker.
    /// </summary>
    private readonly Channel<(string RoomId, string EventKey)> _summaryFetches = Channel.CreateBounded<(string RoomId, string EventKey)>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private async Task QueueSummaryFetchAsync(string roomId, string eventKey, CancellationToken cancellationToken)
    {
        if (_summaryFetches.Writer.TryWrite((roomId, eventKey)))
        {
            return;
        }

        _logger.LogWarning("Agent summary fetch queue is full; the maintenance worker fetches the summary later.");
        await _meeting.MarkSummaryPendingAsync(roomId, cancellationToken);
        await _meeting.ReleaseInboxAsync("agent", eventKey, cancellationToken);
    }

    private async Task ConsumeSummaryFetchesAsync(CancellationToken cancellationToken)
    {
        await foreach (var (roomId, eventKey) in _summaryFetches.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await FetchAgentSummaryAsync(roomId, eventKey, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent summary fetch failed.");
                try
                {
                    await _meeting.ReleaseInboxAsync("agent", eventKey, cancellationToken);
                }
                catch (Exception releaseException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(releaseException, "Could not release Agent inbox claim {EventKey} after a summary fetch failure.", eventKey);
                }
            }
        }
    }

    private async Task FetchAgentSummaryAsync(string roomId, string eventKey, CancellationToken cancellationToken)
    {
                var summary = await _transcript.FetchSummaryAsync(roomId, cancellationToken);
                _logger.LogInformation(
                    "Agent summary completion processed. HasSummary={HasSummary}.",
                    summary is not null);
                if (summary is null || string.IsNullOrWhiteSpace(summary.Summary))
                {
                    await _meeting.MarkSummaryPendingAsync(roomId, cancellationToken);
                    await _meeting.ReleaseInboxAsync("agent", eventKey, cancellationToken);
                }
                else
                {
                    var summaryContext = await _meeting.GetSummaryContextAsync(roomId, cancellationToken);
                    var delivery = summaryContext is null
                        ? null
                        : await _summaryComposer.ComposeAsync(summary, summaryContext, cancellationToken);
                    var stored = await _meeting.StoreSummaryAsync(
                        roomId,
                        summary.Summary,
                        summary.FullTranscriptJson,
                        delivery,
                        cancellationToken);
                    if (!stored)
                    {
                        _logger.LogWarning(
                            "Agent summary could not be matched to a meeting session. RoomId={RoomId}.",
                            roomId);
                        await _meeting.MarkSummaryPendingAsync(roomId, cancellationToken);
                        await _meeting.ReleaseInboxAsync("agent", eventKey, cancellationToken);
                    }
                }
    }
}
