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

        var outcome = await _app.HandleMeetingAsync(
            context.Channel.ClanId,
            context.Channel.Id,
            context.User.Id,
            ["now"],
            ct => PickVoiceAsync(context.Client, context.Channel.ClanId, ct),
            context.CancellationToken);
        await UpdateMeetingMessageAsync(context, MonzeMessageBuilder.Card(outcome, _commandOptions));
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
