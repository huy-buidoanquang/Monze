using Mezon.Net.Sdk.Interactions;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Ui;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task<InteractionInboxLease?> TryClaimInteractionAsync(
        IInteractionContext context,
        string action)
    {
        if (context.Channel.ClanId <= 0
            || context.Channel.Id <= 0
            || context.Interaction.MessageId <= 0)
        {
            _logger.LogWarning(
                "Rejected state-changing interaction without a durable source key. Clan={ClanId}, Channel={ChannelId}, Message={MessageId}, Action={Action}.",
                context.Channel.ClanId,
                context.Channel.Id,
                context.Interaction.MessageId,
                action);
            await UpdateMeetingMessageAsync(
                context,
                MonzeMessageBuilder.Card(
                    MonzeMessages.TitleMeeting,
                    MonzeMessages.InteractionExpired,
                    MonzeTone.Warn));
            return null;
        }

        try
        {
            return await _interactionInbox.TryClaimAsync(
                context.Channel.ClanId,
                context.Channel.Id,
                context.Interaction.MessageId,
                action,
                context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Interaction idempotency claim failed for clan {ClanId}, channel {ChannelId} and action {Action}.",
                context.Channel.ClanId,
                context.Channel.Id,
                action);
            await UpdateMeetingMessageAsync(
                context,
                MonzeMessageBuilder.Card(
                    MonzeMessages.TitleMeeting,
                    MonzeMessages.TemporaryFailure,
                    MonzeTone.Error));
            return null;
        }
    }

    private async Task<bool> CompleteInteractionAsync(
        InteractionInboxLease lease,
        CancellationToken cancellationToken)
    {
        if (await _interactionInbox.CompleteAsync(lease, cancellationToken))
        {
            return true;
        }

        _logger.LogError(
            "Interaction lease was lost before completion. Clan={ClanId}, Channel={ChannelId}, Message={MessageId}, Action={Action}.",
            lease.ClanId,
            lease.ChannelId,
            lease.MessageId,
            lease.Action);
        return false;
    }

    private async Task ReleaseInteractionAsync(
        InteractionInboxLease lease,
        CancellationToken cancellationToken)
    {
        try
        {
            await _interactionInbox.ReleaseAsync(lease, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Could not release interaction lease. Message={MessageId}, Action={Action}.",
                lease.MessageId,
                lease.Action);
        }
    }

    private async Task MarkInteractionUncertainAsync(InteractionInboxLease lease)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_runtimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (!await _interactionInbox.MarkUncertainAsync(lease, timeout.Token))
            {
                _logger.LogError(
                    "Interaction lease was lost before uncertainty was recorded. Message={MessageId}, Action={Action}.",
                    lease.MessageId,
                    lease.Action);
            }
        }
        catch (Exception ex) when (!timeout.IsCancellationRequested || !_runtimeToken.IsCancellationRequested)
        {
            _logger.LogError(
                ex,
                "Could not record uncertain interaction. Message={MessageId}, Action={Action}.",
                lease.MessageId,
                lease.Action);
        }
    }
}
