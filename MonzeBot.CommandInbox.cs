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
            await _commandInbox.CompleteAsync(lease.Value, context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            await ReleaseCommandAsync(lease.Value, context.CancellationToken);
        }
        catch
        {
            await ReleaseCommandAsync(lease.Value, context.CancellationToken);
            throw;
        }
    }

    private async Task ReleaseCommandAsync(
        CommandInboxLease lease,
        CancellationToken cancellationToken)
    {
        try
        {
            await _commandInbox.ReleaseAsync(lease, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Could not release command inbox lease for message {MessageId}.",
                lease.MessageId);
        }
    }
}
