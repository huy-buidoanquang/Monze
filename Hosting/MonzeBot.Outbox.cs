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
    private async Task RunOutboxWorkerAsync(
        MezonClient client,
        CancellationToken cancellationToken)
    {
        var nextReconcile = _time.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (await FlushOutboxBatchAsync(client, cancellationToken))
                {
                }

                if (_time.GetElapsedTime(nextReconcile) >= TimeSpan.Zero)
                {
                    nextReconcile = _time.GetTimestamp() + (long)(_timings.OutboxReconcileInterval.TotalSeconds * _time.TimestampFrequency);
                    await ReconcileUncertainOutboxAsync(client, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Outbox worker iteration failed; retrying.");
            }

            await Task.Delay(_timings.OutboxPollInterval, _time, cancellationToken);
        }
    }

    private async Task<bool> FlushOutboxBatchAsync(
        MezonClient client,
        CancellationToken cancellationToken)
    {
        var items = await _outbox.ClaimDueOutboxAsync(cancellationToken);
        if (items.Count == 0)
        {
            return false;
        }

        MonzeMetrics.OutboxClaimed.Add(items.Count);

        var maxConcurrency = Math.Clamp(
            _configuration.GetValue("Monze:Outbox:MaxConcurrency", 32),
            1,
            128);
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cancellationToken
            },
            async (item, ct) => await DeliverOutboxAsync(client, item, ct));
        return true;
    }

    private async ValueTask DeliverOutboxAsync(
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
                cancellationToken,
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
                cancellationToken,
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
                    cancellationToken,
                    "delivery-uncertain");
                return;
            }

            await TryCompleteOutboxAsync(
                item.Id,
                item.LeaseToken,
                ackMessageId,
                false,
                cancellationToken,
                null);
            MonzeMetrics.OutboxDelivered.Add(1);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Outbox {OutboxId} did not get an ack", item.Id);
            var failure = OutboxDeliveryFailure.Classify(ex);
            if (failure == OutboxFailureKind.Uncertain)
            {
                // The message may be on the channel: reconcile, never resend blindly.
                MonzeMetrics.OutboxUncertain.Add(1);
                await TryCompleteOutboxAsync(item.Id, item.LeaseToken, null, true, cancellationToken, "delivery-uncertain");
                return;
            }

            MonzeMetrics.OutboxFailed.Add(1);
            if (failure == OutboxFailureKind.NotSent)
            {
                // Nothing left the process (the socket is closed): retry until it is back.
                await TryCompleteOutboxAsync(item.Id, item.LeaseToken, null, false, cancellationToken, "delivery-not-sent", countsAsAttempt: false);
                return;
            }

            var hold = failure == OutboxFailureKind.Rejected
                || OutboxPolicy.Decide(kind, false, item.Attempts + 1) == OutboxAction.HoldForAdmin;
            await TryCompleteOutboxAsync(
                item.Id,
                item.LeaseToken,
                null,
                hold,
                cancellationToken,
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

    private async Task TryCompleteOutboxAsync(
        long id,
        string leaseToken,
        long? externalMessageId,
        bool failed,
        CancellationToken cancellationToken,
        string? errorCode,
        bool countsAsAttempt = true)
    {
        try
        {
            await _outbox.CompleteOutboxAsync(
                id,
                leaseToken,
                externalMessageId,
                failed,
                cancellationToken,
                errorCode,
                countsAsAttempt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Outbox {OutboxId} completion timed out; lease will be reclaimed.", id);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Outbox {OutboxId} completion failed; lease will be reclaimed.", id);
        }
    }

}

