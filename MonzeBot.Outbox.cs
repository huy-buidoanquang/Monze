using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
        var pollMilliseconds = Math.Clamp(
            _configuration.GetValue("Monze:Outbox:PollMilliseconds", 250),
            100,
            5_000);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (await FlushOutboxBatchAsync(client, cancellationToken))
                {
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

            await Task.Delay(pollMilliseconds, cancellationToken);
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
        var dueLag = DateTimeOffset.UtcNow - item.DueAt;
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
            var content = string.IsNullOrWhiteSpace(item.ContentJson)
                ? item.Kind.Equals("MeetingSummary", StringComparison.OrdinalIgnoreCase)
                    ? MonzeMessageBuilder.MeetingSummary(item.Body, item.ReplyToMessageId)
                    : MonzeMessageBuilder.Card(item.Kind, item.Body, MonzeTone.Info)
                : Mezon.Net.Client.MessageContent.Parse(item.ContentJson);
            var ack = await channel.SendAsync(
                content,
                mentionEveryone: item.MentionEveryone,
                mentions: item.MentionEveryone ? MonzeMentionMetadata.Here : null);
            await TryCompleteOutboxAsync(
                item.Id,
                item.LeaseToken,
                ack.MessageId,
                false,
                cancellationToken,
                null);
            if (item.MeetingSessionId is long sessionId)
            {
                await _meeting.SetSessionNotificationMessageAsync(
                    sessionId,
                    item.ChannelId,
                    ack.MessageId,
                    cancellationToken);
            }
            MonzeMetrics.OutboxDelivered.Add(1);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Outbox {OutboxId} did not get an ack", item.Id);
            var uncertain = IsUncertainDeliveryFailure(ex);
            if (uncertain)
            {
                MonzeMetrics.OutboxUncertain.Add(1);
            }
            else
            {
                MonzeMetrics.OutboxFailed.Add(1);
            }
            var hold = uncertain || OutboxPolicy.Decide(kind, false, item.Attempts)
                == OutboxAction.HoldForAdmin;
            await TryCompleteOutboxAsync(
                item.Id,
                item.LeaseToken,
                null,
                hold,
                cancellationToken,
                uncertain ? "delivery-uncertain" : "delivery-failed");
        }
    }

    private async Task TryCompleteOutboxAsync(
        long id,
        string leaseToken,
        long? externalMessageId,
        bool failed,
        CancellationToken cancellationToken,
        string? errorCode)
    {
        try
        {
            await _outbox.CompleteOutboxAsync(
                id,
                leaseToken,
                externalMessageId,
                failed,
                cancellationToken,
                errorCode);
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

    private static bool IsUncertainDeliveryFailure(Exception exception)
        => exception is TimeoutException
            or TaskCanceledException
            or HttpRequestException
            or IOException
            or System.Net.Sockets.SocketException;

}

