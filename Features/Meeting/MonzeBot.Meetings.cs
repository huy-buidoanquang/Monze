using System.Collections.Concurrent;
using Mezon.Net.Client;
using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;
using Monze.Ui;
using Microsoft.Extensions.Logging;
using SdkMezonClient = Mezon.Net.Sdk.MezonClient;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task HandleMeetingAsync(ICommandContext context)
        => await ExecuteCommandOnceAsync(context, () => HandleMeetingCoreAsync(context));

    private async Task HandleMeetingCoreAsync(
        ICommandContext context,
        IReadOnlyList<string>? commandArguments = null)
    {
        var args = commandArguments ?? context.Args;
        var clanId = context.Clan?.Id ?? 0;
        if (clanId == 0)
        {
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleMeeting,
                MonzeMessages.UnknownClan,
                MonzeTone.Error));
            return;
        }

        if (!_commandRateLimiter.TryAcquire(
                clanId,
                context.Author.Id,
                MonzeCommandNames.Meeting,
                _time.GetUtcNow(),
                out var retryAfter))
        {
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleRateLimited,
                MonzeMessages.RateLimited(retryAfter),
                MonzeTone.Warn));
            return;
        }

        try
        {
            if (args.Count > 0
                && args[0].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase))
            {
                var help = await _app.HandleMonzeAsync(
                    clanId,
                    context.Channel.Id,
                    context.Author.Id,
                    new CommandArguments(new[] { MonzeCommandNames.Help, MonzeCommandNames.Meeting }),
                    context.CancellationToken);
                await ReplyCommandAsync(context, MonzeMessageBuilder.Card(help, _commandOptions));
                return;
            }

            var outcome = await _app.HandleMeetingAsync(
                clanId,
                context.Channel.Id,
                context.Author.Id,
                args,
                ct => PickVoiceAsync(context, clanId, ct),
                context.CancellationToken);
            if (outcome.MeetingInvitation is { } invitation)
            {
                await DeliverMeetingInvitationAsync(
                    context.Channel,
                    invitation,
                    outcome.Text,
                    context.CancellationToken);
            }
            else
            {
                await ReplyCommandAsync(context, MonzeMessageBuilder.Card(outcome, _commandOptions));
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Meeting command failed for clan {ClanId} and channel {ChannelId}.", clanId, context.Channel.Id);
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleMeeting,
                MonzeMessages.TemporaryFailure,
                MonzeTone.Error));
        }
    }

    private async Task HandleSummaryAsync(ICommandContext context)
        => await ExecuteCommandOnceAsync(context, () => HandleSummaryCoreAsync(context));

    private async Task HandleSummaryCoreAsync(
        ICommandContext context,
        IReadOnlyList<string>? commandArguments = null)
    {
        var args = commandArguments ?? context.Args;
        var clanId = context.Clan?.Id ?? 0;
        if (clanId == 0)
        {
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleSummary,
                MonzeMessages.UnknownClan,
                MonzeTone.Error));
            return;
        }

        if (!_commandRateLimiter.TryAcquire(
                clanId,
                context.Author.Id,
                MonzeCommandNames.Summary,
                _time.GetUtcNow(),
                out var retryAfter))
        {
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleRateLimited,
                MonzeMessages.RateLimited(retryAfter),
                MonzeTone.Warn));
            return;
        }

        try
        {
            if (args.Count > 0
                && args[0].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase))
            {
                var help = await _app.HandleMonzeAsync(
                    clanId,
                    context.Channel.Id,
                    context.Author.Id,
                    new CommandArguments(new[] { MonzeCommandNames.Help, MonzeCommandNames.Summary }),
                    context.CancellationToken);
                await ReplyCommandAsync(context, MonzeMessageBuilder.Card(help, _commandOptions));
                return;
            }

            var outcome = await _app.HandleSummaryAsync(
                clanId,
                context.Author.Id,
                args,
                context.CancellationToken);

            if (outcome.MeetingSummary is { } savedSummary)
            {
                var delivery = await _summaryComposer.ComposeAsync(
                    savedSummary,
                    context.CancellationToken);
                if (delivery is not null)
                {
                    await context.ReplyAsync(MessageContent.Parse(delivery.SummaryContentJson));
                    await context.Channel.SendAsync(MessageContent.Parse(delivery.ActionItemsContentJson));
                    return;
                }
            }

            await ReplyCommandAsync(context, MonzeMessageBuilder.Card(outcome, _commandOptions));
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Summary command failed for channel {ChannelId}.", context.Channel.Id);
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleSummary,
                MonzeMessages.TemporaryFailure,
                MonzeTone.Error));
        }
    }

}
