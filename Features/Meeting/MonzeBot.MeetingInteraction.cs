using Mezon.Net.Sdk.Interactions;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Ui;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task HandleMeetingListAsync(IInteractionContext context)
    {
        if (!await EnsurePrivateInteractionAsync(context))
        {
            return;
        }

        var outcome = await _app.HandleMeetingAsync(
            context.Channel.ClanId,
            context.Channel.Id,
            context.User.Id,
            Array.Empty<string>(),
            static _ => Task.FromResult<MeetingVoiceCandidate?>(null),
            context.CancellationToken);
        await UpdateMeetingMessageAsync(context, MonzeMessageBuilder.Card(outcome, _commandOptions));
    }

    private async Task HandleMeetingNowInteractionAsync(IInteractionContext context)
    {
        if (!await EnsurePrivateInteractionAsync(context))
        {
            return;
        }

        InteractionInboxLease? lease = await TryClaimInteractionAsync(context, MeetingButtonId.StartNow);
        if (lease is null)
        {
            return;
        }

        try
        {
            var outcome = await _app.HandleMeetingAsync(
                context.Channel.ClanId,
                context.Channel.Id,
                context.User.Id,
                ["now"],
                ct => PickVoiceAsync(context.Client, context.Channel.ClanId, ct),
                context.CancellationToken);
            if (outcome.MeetingInvitation is not { } invitation)
            {
                await ReleaseInteractionAsync(lease.Value, context.CancellationToken);
                lease = null;
                await UpdateMeetingMessageAsync(context, MonzeMessageBuilder.Card(outcome, _commandOptions));
                return;
            }

            var messageId = await DeliverMeetingInvitationAsync(
                context.Channel,
                invitation,
                outcome.Text,
                context.CancellationToken);
            if (messageId <= 0)
            {
                await MarkInteractionUncertainAsync(lease.Value);
                lease = null;
                await UpdateMeetingMessageAsync(
                    context,
                    MonzeMessageBuilder.Card(
                        MonzeMessages.TitleMeeting,
                        MonzeMessages.TemporaryFailure,
                        MonzeTone.Error));
                return;
            }

            if (!await CompleteInteractionAsync(lease.Value, context.CancellationToken))
            {
                await MarkInteractionUncertainAsync(lease.Value);
                lease = null;
                return;
            }

            lease = null;
            await UpdateMeetingMessageAsync(context, MonzeMessageBuilder.Card(outcome, _commandOptions));
        }
        catch
        {
            if (lease is { } uncertainLease)
            {
                await MarkInteractionUncertainAsync(uncertainLease);
            }

            throw;
        }
    }

    private async Task HandleMeetingCancelInteractionAsync(IInteractionContext context)
    {
        if (!await EnsurePrivateInteractionAsync(context))
        {
            return;
        }

        if (context.Interaction is not ButtonInteraction button
            || !MeetingButtonId.TryReadCancelId(button.CustomId, out var scheduleId))
        {
            await UpdateMeetingMessageAsync(context, MonzeMessageBuilder.Card(
                MonzeMessages.TitleMeeting,
                MonzeMessages.MeetingScheduleNotFound,
                MonzeTone.Warn));
            return;
        }

        var outcome = await _app.HandleMeetingAsync(
            context.Channel.ClanId,
            context.Channel.Id,
            context.User.Id,
            ["cancel", scheduleId.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            static _ => Task.FromResult<MeetingVoiceCandidate?>(null),
            context.CancellationToken);
        await UpdateMeetingMessageAsync(context, MonzeMessageBuilder.Card(outcome, _commandOptions));
    }
}
