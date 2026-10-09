using Mezon.Net.Sdk.Commands;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Ui;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task ExecuteCommandOnceAsync(
        ICommandContext context,
        string module,
        Func<Task> handler)
    {
        var startedAt = _time.GetTimestamp();
        if (Interlocked.Increment(ref _commandsInFlight) > _maxCommandsInFlight)
        {
            // Overloaded (the SDK starts a handler per message without a bound):
            // drop the command unanswered rather than queue it behind the
            // upstream budget, so the bot recovers as soon as the burst ends.
            Interlocked.Decrement(ref _commandsInFlight);
            _logger.LogDebug("Command shed: {InFlight} commands already in flight.", _maxCommandsInFlight);
            MonzeMetrics.RecordCommand(MonzeCommandMetricTags.Module(module), MonzeCommandMetricTags.Shed, _time.GetElapsedTime(startedAt));
            return;
        }

        var outcome = MonzeCommandMetricTags.Failed;
        MonzeMetrics.CommandInflight.Add(1);
        try
        {
            outcome = await ExecuteCommandOnceCoreAsync(context, handler);
        }
        finally
        {
            Interlocked.Decrement(ref _commandsInFlight);
            MonzeMetrics.CommandInflight.Add(-1);
            MonzeMetrics.RecordCommand(
                MonzeCommandMetricTags.Module(module),
                outcome,
                _time.GetElapsedTime(startedAt));
        }
    }

    private async Task<string> ExecuteCommandOnceCoreAsync(
        ICommandContext context,
        Func<Task> handler)
    {
        var clanId = context.Clan?.Id ?? 0;
        var channelId = context.Channel.Id;
        var messageId = context.Message.Id;
        if (clanId <= 0 || channelId <= 0 || messageId <= 0)
        {
            await handler();
            return MonzeCommandMetricTags.Completed;
        }

        CommandInboxLease? lease;
        try
        {
            lease = await _commandInbox.TryClaimAsync(
                clanId,
                channelId,
                messageId,
                context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return MonzeCommandMetricTags.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Command idempotency claim failed for clan {ClanId} and channel {ChannelId}.",
                clanId,
                channelId);
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleMonze,
                MonzeMessages.TemporaryFailure,
                MonzeTone.Error));
            return MonzeCommandMetricTags.ClaimFailed;
        }

        if (lease is null)
        {
            return MonzeCommandMetricTags.Duplicate;
        }

        try
        {
            await handler();
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            await MarkCommandUncertainAsync(lease.Value);
            return MonzeCommandMetricTags.Cancelled;
        }
        catch
        {
            await MarkCommandUncertainAsync(lease.Value);
            throw;
        }

        try
        {
            // Not the command's token: an answered command whose completion is dropped
            // while stopping stays 'processing' and a redelivery answers it again (WF-08).
            using var timeout = NewOutcomeTimeout();
            if (!await _commandInbox.CompleteAsync(lease.Value, timeout.Token))
            {
                _logger.LogError(
                    "Command inbox lease was lost before completion. Clan={ClanId}, Channel={ChannelId}, Message={MessageId}.",
                    lease.Value.ClanId,
                    lease.Value.ChannelId,
                    lease.Value.MessageId);
                await MarkCommandUncertainAsync(lease.Value);
                return MonzeCommandMetricTags.Uncertain;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Command inbox completion failed after command handling. Clan={ClanId}, Channel={ChannelId}, Message={MessageId}.",
                lease.Value.ClanId,
                lease.Value.ChannelId,
                lease.Value.MessageId);
            await MarkCommandUncertainAsync(lease.Value);
            return MonzeCommandMetricTags.Uncertain;
        }

        return MonzeCommandMetricTags.Completed;
    }

    private async Task MarkCommandUncertainAsync(CommandInboxLease lease)
    {
        // Not on the stopping token: stopping is when this matters (WF-08).
        using var timeout = NewOutcomeTimeout();
        try
        {
            if (!await _commandInbox.MarkUncertainAsync(lease, timeout.Token))
            {
                _logger.LogError(
                    "Command inbox lease was lost before uncertainty was recorded. Message={MessageId}.",
                    lease.MessageId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not record uncertain command for message {MessageId}.",
                lease.MessageId);
        }
    }
}
