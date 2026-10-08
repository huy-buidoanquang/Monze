using System.Collections.Concurrent;
using System.Diagnostics;
using Mezon.Net.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Domain;
using Monze.Ui;

namespace Monze;

public sealed partial class MonzeBot
{
    /// <summary>A claimed row gets a send slot within this time, well inside its 60 s lease.</summary>
    private static readonly TimeSpan OutboxClaimHorizon = TimeSpan.FromSeconds(20);

    /// <summary>How often the 60 s leases of rows still being delivered are extended.</summary>
    private static readonly TimeSpan OutboxLeaseRenewal = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Delivers due outbox rows as a pipeline: up to Monze:Outbox:MaxConcurrency
    /// deliveries run at once, and once half of them have finished the free
    /// slots are refilled (a batch no longer waits for its slowest send). Only
    /// rows that get a send slot from <paramref name="pacer"/> within
    /// <see cref="OutboxClaimHorizon"/> are claimed, and the leases of rows
    /// still being delivered (a send can also wait for the SDK's transport
    /// budget, which commands share) are renewed while this process lives, so
    /// only a crashed delivery is ever reconciled.
    /// </summary>
    private async Task RunOutboxWorkerAsync(
        MezonClient client,
        UpstreamPacer pacer,
        CancellationToken cancellationToken)
    {
        var maxConcurrency = Math.Clamp(
            _configuration.GetValue("Monze:Outbox:MaxConcurrency", 32),
            1,
            128);
        var refill = Math.Max(1, maxConcurrency / 2);
        var deliveries = new List<Task>(maxConcurrency);
        var leases = new ConcurrentDictionary<long, string>();
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewOutboxLeasesAsync(leases, renewalStop.Token);
        Task? poll = null;
        var nextReconcile = _time.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            var moreDue = false;
            try
            {
                deliveries.RemoveAll(task => task.IsCompleted && ObserveOutboxDelivery(task));
                var free = maxConcurrency - deliveries.Count;
                var limit = Math.Min(free, pacer.Available(OutboxClaimHorizon));
                if (free >= refill && limit > 0)
                {
                    var items = await _outbox.ClaimDueOutboxAsync(cancellationToken, limit: limit);
                    MonzeMetrics.OutboxClaimed.Add(items.Count);
                    foreach (var item in items)
                    {
                        deliveries.Add(DeliverOutboxAsync(client, pacer, item, leases, cancellationToken));
                    }

                    moreDue = items.Count == limit;
                }

                if (_time.GetElapsedTime(nextReconcile) >= TimeSpan.Zero)
                {
                    nextReconcile = _time.GetTimestamp() + (long)(_timings.OutboxReconcileInterval.TotalSeconds * _time.TimestampFrequency);
                    await ReconcileUncertainOutboxAsync(client, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Outbox worker iteration failed; retrying.");
            }

            if (moreDue && maxConcurrency - deliveries.Count >= refill)
            {
                continue;
            }

            try
            {
                // Wake on the poll interval or as soon as a delivery finishes.
                poll ??= Task.Delay(_timings.OutboxPollInterval, _time, cancellationToken);
                await (deliveries.Count == 0 ? poll : Task.WhenAny(deliveries.Append(poll)));
                if (poll.IsCompleted)
                {
                    await poll;
                    poll = null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        foreach (var delivery in deliveries)
        {
            try
            {
                await delivery;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Outbox delivery failed.");
            }
        }

        await renewalStop.CancelAsync();
        await renewal;
    }

    private async Task RenewOutboxLeasesAsync(ConcurrentDictionary<long, string> leases, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(OutboxLeaseRenewal, _time, cancellationToken);
                if (!leases.IsEmpty)
                {
                    await _outbox.RenewOutboxLeasesAsync(new Dictionary<long, string>(leases), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Outbox lease renewal failed; retrying.");
            }
        }
    }

    private bool ObserveOutboxDelivery(Task delivery)
    {
        if (delivery.Exception is { } exception)
        {
            _logger.LogWarning(exception.GetBaseException(), "Outbox delivery failed.");
        }

        return true;
    }

    private async Task DeliverOutboxAsync(
        MezonClient client,
        UpstreamPacer pacer,
        DueOutbox item,
        ConcurrentDictionary<long, string> leases,
        CancellationToken cancellationToken)
    {
        leases[item.Id] = item.LeaseToken;
        try
        {
            // The slot is reserved before the first await, so the next claim sees it.
            var wait = pacer.Reserve();
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, _time, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Stopping before the send: hand the row back now instead of after its lease.
                    await TryCompleteOutboxAsync(item.Id, item.LeaseToken, null, false, "delivery-not-sent", countsAsAttempt: false);
                    throw;
                }
            }

            await DeliverOutboxAsync(client, item, cancellationToken);
        }
        finally
        {
            leases.TryRemove(item.Id, out _);
        }
    }

    private async Task DeliverOutboxAsync(
        MezonClient client,
        DueOutbox item,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _outboxInFlight);
        var dueLag = _time.GetUtcNow() - item.DueAt;
        if (dueLag >= TimeSpan.Zero)
        {
            MonzeMetrics.OutboxDueLagMilliseconds.Record(dueLag.TotalMilliseconds);
        }

        try
        {
            await DeliverOutboxCoreAsync(client, item, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _outboxInFlight);
            MonzeMetrics.OutboxAttemptDurationMilliseconds.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    private async ValueTask DeliverOutboxCoreAsync(
        MezonClient client,
        DueOutbox item,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<OutboxKind>(item.Kind, true, out var kind))
        {
            MonzeMetrics.OutboxFailed.Add(1);
            await TryCompleteOutboxAsync(
                item.Id,
                item.LeaseToken,
                null,
                true,
                "invalid-kind");
            return;
        }

        var action = OutboxPolicy.Decide(
            kind,
            item.ExternalMessageId is not null,
            item.Attempts);
        if (action is OutboxAction.AlreadyDelivered or OutboxAction.HoldForAdmin)
        {
            if (action == OutboxAction.HoldForAdmin)
            {
                MonzeMetrics.OutboxFailed.Add(1);
            }

            await TryCompleteOutboxAsync(
                item.Id,
                item.LeaseToken,
                item.ExternalMessageId,
                action == OutboxAction.HoldForAdmin,
                action == OutboxAction.HoldForAdmin ? "retry-limit" : null);
            return;
        }

        try
        {
            var channel = await client.GetChannelAsync(item.ChannelId, cancellationToken);
            var content = BuildOutboxContent(item);
            var references = MeetingReplyReference.Create(
                item,
                client.BotId,
                client.Username);
            var ack = await channel.SendAsync(
                content,
                mentionEveryone: item.MentionEveryone,
                mentions: item.MentionEveryone ? MonzeMentionMetadata.Here : null,
                references: references);
            var ackMessageId = TryReadMessageId(ack);
            if (ackMessageId <= 0)
            {
                MonzeMetrics.OutboxUncertain.Add(1);
                await TryCompleteOutboxAsync(
                    item.Id,
                    item.LeaseToken,
                    null,
                    true,
                    "delivery-uncertain");
                return;
            }

            await TryCompleteOutboxAsync(
                item.Id,
                item.LeaseToken,
                ackMessageId,
                false,
                null);
            MonzeMetrics.OutboxDelivered.Add(1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping while the send may be on the wire: reconcile it rather than resend it.
            await TryCompleteOutboxAsync(item.Id, item.LeaseToken, null, true, "delivery-uncertain");
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Outbox {OutboxId} did not get an ack", item.Id);
            var failure = OutboxDeliveryFailure.Classify(ex);
            if (failure == OutboxFailureKind.Uncertain)
            {
                // The message may be on the channel: reconcile, never resend blindly.
                MonzeMetrics.OutboxUncertain.Add(1);
                await TryCompleteOutboxAsync(item.Id, item.LeaseToken, null, true, "delivery-uncertain");
                return;
            }

            MonzeMetrics.OutboxFailed.Add(1);
            if (failure == OutboxFailureKind.NotSent)
            {
                // Nothing left the process (the socket is closed): retry until it is back.
                await TryCompleteOutboxAsync(item.Id, item.LeaseToken, null, false, "delivery-not-sent", countsAsAttempt: false);
                return;
            }

            var hold = failure == OutboxFailureKind.Rejected
                || OutboxPolicy.Decide(kind, false, item.Attempts + 1) == OutboxAction.HoldForAdmin;
            await TryCompleteOutboxAsync(
                item.Id,
                item.LeaseToken,
                null,
                hold,
                failure == OutboxFailureKind.Rejected ? "delivery-rejected" : hold ? "retry-limit" : "delivery-failed");
        }
    }

    /// <summary>The content an outbox row is sent with; reconciliation compares against the same rendering.</summary>
    private static Mezon.Net.Client.MessageContent BuildOutboxContent(DueOutbox item)
    {
        var content = string.IsNullOrWhiteSpace(item.ContentJson)
            ? item.Kind.Equals("MeetingSummary", StringComparison.OrdinalIgnoreCase)
                ? MonzeMessageBuilder.MeetingSummary(item.Body, item.ReplyToMessageId)
                : MonzeMessageBuilder.Card(item.Kind, item.Body, MonzeTone.Info)
            : Mezon.Net.Client.MessageContent.Parse(item.ContentJson);
        return item.ReplyToMessageId is long replyToMessageId
            ? MessageContentReply.Apply(content, replyToMessageId)
            : content;
    }

    /// <summary>
    /// Records how a delivery attempt ended on an <see cref="OutcomeTimeout"/>,
    /// not on the worker's stopping token: an acked message whose completion
    /// is dropped at shutdown would stay leased and be reconciled or resent
    /// (WF-05), and a timeout while the bot runs would do the same whenever
    /// the database is slow.
    /// </summary>
    private async Task TryCompleteOutboxAsync(
        long id,
        string leaseToken,
        long? externalMessageId,
        bool failed,
        string? errorCode,
        bool countsAsAttempt = true)
    {
        using var timeout = NewOutcomeTimeout();
        try
        {
            await _outbox.CompleteOutboxAsync(
                id,
                leaseToken,
                externalMessageId,
                failed,
                timeout.Token,
                errorCode,
                countsAsAttempt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Outbox {OutboxId} completion failed; the row is reconciled once its lease expires.", id);
        }
    }

}

