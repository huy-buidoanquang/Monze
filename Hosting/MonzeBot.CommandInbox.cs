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
        Func<Task> handler)
    {
        var clanId = context.Clan?.Id ?? 0;
        var channelId = context.Channel.Id;
        var messageId = context.Message.Id;
        if (clanId <= 0 || channelId <= 0 || messageId <= 0)
        {
            await handler();
            return;
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
            return;
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
            return;
        }

        if (lease is null)
        {
            return;
        }

        try
        {
            await handler();
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            await MarkCommandUncertainAsync(lease.Value);
            return;
        }
        catch
        {
            await MarkCommandUncertainAsync(lease.Value);
            throw;
        }

        try
        {
            if (!await _commandInbox.CompleteAsync(lease.Value, context.CancellationToken))
            {
                _logger.LogError(
                    "Command inbox lease was lost before completion. Clan={ClanId}, Channel={ChannelId}, Message={MessageId}.",
                    lease.Value.ClanId,
                    lease.Value.ChannelId,
                    lease.Value.MessageId);
                await MarkCommandUncertainAsync(lease.Value);
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
        }
    }

    private async Task MarkCommandUncertainAsync(CommandInboxLease lease)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_runtimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (!await _commandInbox.MarkUncertainAsync(lease, timeout.Token))
            {
                _logger.LogError(
                    "Command inbox lease was lost before uncertainty was recorded. Message={MessageId}.",
                    lease.MessageId);
            }
        }
        catch (Exception ex) when (!timeout.IsCancellationRequested || !_runtimeToken.IsCancellationRequested)
        {
            _logger.LogError(
                ex,
                "Could not record uncertain command for message {MessageId}.",
                lease.MessageId);
        }
    }
}
