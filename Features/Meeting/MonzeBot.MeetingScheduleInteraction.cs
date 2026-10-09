using Mezon.Net.Sdk.Interactions;
using Mezon.Net.Client;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Ui;

namespace Monze;

public sealed partial class MonzeBot
{
    private Task UpdateMeetingMessageAsync(
        IInteractionContext context,
        MessageContent content)
        => UpdatePrivateInteractionAsync(context, content);

    private async Task HandleMeetingScheduleFormAsync(IInteractionContext context)
    {
        if (!await EnsurePrivateInteractionAsync(context))
        {
            return;
        }

        await UpdateMeetingMessageAsync(context, MonzeMessageBuilder.MeetingScheduleForm());
    }

    private async Task HandleMeetingScheduleSubmitAsync(IInteractionContext context)
    {
        if (!await EnsurePrivateInteractionAsync(context))
        {
            return;
        }

        InteractionInboxLease? lease = null;

        _logger.LogInformation(
            "Meeting schedule submit interaction received. MessageId={MessageId}, ChannelId={ChannelId}, HasCachedMessage={HasCachedMessage}",
            context.Interaction.MessageId,
            context.Interaction.ChannelId,
            context.Message is not null);

        try
        {
            if (context.Interaction is not ButtonInteraction button)
            {
                await UpdateMeetingMessageAsync(
                    context,
                    MonzeMessageBuilder.MeetingScheduleForm("Hãy nhập đủ thông tin lịch họp."));
                return;
            }

            if (!MeetingScheduleFormParser.TryRead(
                    button.ExtraData,
                    out var name,
                    out var date,
                    out var time,
                    out var kind,
                    out var error))
            {
                _logger.LogWarning(
                    "Meeting schedule form validation failed: {Error}; Input={Input}",
                    error,
                    MeetingScheduleFormParser.DescribeInput(button.ExtraData));
                await UpdateMeetingMessageAsync(context, MonzeMessageBuilder.MeetingScheduleForm(error));
                return;
            }

            _logger.LogInformation(
                "Meeting schedule form validated. NameLength={NameLength}, Date={Date}, Time={Time}, Kind={Kind}",
                name.Length,
                date,
                time,
                kind);

            lease = await TryClaimInteractionAsync(context, MeetingButtonId.ScheduleSubmit);
            if (lease is null)
            {
                return;
            }

            var nameParts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var args = new List<string>(nameParts.Length + 3);
            args.AddRange(nameParts);
            args.Add(date);
            args.Add(time);
            args.Add(kind.ToString().ToLowerInvariant());

            var outcome = await _app.HandleMeetingAsync(
                context.Channel.ClanId,
                context.Channel.Id,
                context.User.Id,
                args,
                static _ => Task.FromResult<MeetingVoiceCandidate?>(null),
                context.CancellationToken);
            _logger.LogInformation("Meeting schedule command completed. Title={Title}", outcome.Title);
            if (outcome.Tone != MonzeTone.Ok)
            {
                await ReleaseInteractionAsync(lease.Value, context.CancellationToken);
                lease = null;
                await UpdateMeetingMessageAsync(context, MonzeMessageBuilder.Card(outcome, _commandOptions));
                return;
            }

            if (!await CompleteInteractionAsync(lease.Value))
            {
                await MarkInteractionUncertainAsync(lease.Value);
                lease = null;
                return;
            }

            lease = null;
            await UpdateMeetingMessageAsync(context, MonzeMessageBuilder.Card(outcome, _commandOptions));
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            if (lease is { } cancelledLease)
            {
                await MarkInteractionUncertainAsync(cancelledLease);
            }

            throw;
        }
        catch (Exception exception)
        {
            if (lease is { } uncertainLease)
            {
                await MarkInteractionUncertainAsync(uncertainLease);
            }

            _logger.LogError(exception, "Meeting schedule submit interaction failed.");
            try
            {
                await UpdateMeetingMessageAsync(
                    context,
                    MonzeMessageBuilder.MeetingScheduleForm("Không thể lưu lịch lúc này. Vui lòng thử lại."));
            }
            catch (Exception updateException)
            {
                _logger.LogError(updateException, "Meeting schedule error response failed.");
            }
        }
    }

    private async Task HandleMeetingScheduleCancelAsync(IInteractionContext context)
    {
        if (!await EnsurePrivateInteractionAsync(context))
        {
            return;
        }

        await DeletePrivateInteractionAsync(context);
    }
}
