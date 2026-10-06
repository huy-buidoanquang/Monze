using Microsoft.Extensions.Logging;
using Mezon.Net.Client;
using Mezon.Net.Core;
using Mezon.Net.Sdk.Commands;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;
using Monze.Ui;
using System.Text;

namespace Monze;

public sealed partial class MonzeBot
{
    private Task HandleMonzeAsync(ICommandContext context)
        => ExecuteCommandOnceAsync(
            context,
            () => HandleMonzeCoreAsync(
                context,
                new CommandArguments(context.Args),
                ResolveMonzeCommand(context)));

    private Task HandleDirectMonzeAsync(ICommandContext context, string module)
        => ExecuteCommandOnceAsync(
            context,
            () => HandleMonzeCoreAsync(
                context,
                new CommandArguments(module, context.Args),
                module));

    private Task HandleDirectHelpAsync(ICommandContext context)
        => ExecuteCommandOnceAsync(
            context,
            () => HandleMonzeCoreAsync(
                context,
                new CommandArguments(MonzeCommandNames.Help, context.Args),
                MonzeCommandNames.Help));

    private async Task HandleMonzeCoreAsync(
        ICommandContext context,
        CommandArguments args,
        string commandKey)
    {
        var clanId = context.Clan?.Id ?? 0;
        if (clanId == 0)
        {
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleMonze,
                MonzeMessages.UnknownClan,
                MonzeTone.Error));
            return;
        }

        if (_commandOptions.HasRoot && args.Length > 0)
        {
            if (args[0].Equals(MonzeCommandNames.Meeting, StringComparison.OrdinalIgnoreCase))
            {
                await HandleMeetingCoreAsync(context, args.Slice(1));
                return;
            }

            if (args[0].Equals(MonzeCommandNames.Summary, StringComparison.OrdinalIgnoreCase))
            {
                await HandleSummaryCoreAsync(context, args.Slice(1));
                return;
            }
        }

        if (args.Length > 0
            && IsAvatarCommand(args[0])
            && !(args.Length > 1 && args[1].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase)))
        {
            await HandleAvatarAsync(context, args.Slice(1));
            return;
        }

        var isAiCommand = IsAiCommand(args);
        if (!_commandRateLimiter.TryAcquire(
                clanId,
                context.Author.Id,
                commandKey,
                DateTimeOffset.UtcNow,
                out var retryAfter))
        {
            var rateLimitResponse = MonzeMessageBuilder.Card(
                MonzeMessages.TitleRateLimited,
                MonzeMessages.RateLimited(retryAfter),
                MonzeTone.Warn);
            await ReplyCommandAsync(context, rateLimitResponse);

            return;
        }

        var aiLoadingMessageId = 0L;
        try
        {
            if (isAiCommand)
            {
                aiLoadingMessageId = await ReplyCommandAsync(context, MonzeMessageBuilder.AiLoading());
                _logger.LogInformation(
                    "AI loading response sent. Channel={ChannelId}, SourceMessage={SourceMessageId}, LoadingMessage={LoadingMessageId}.",
                    context.Channel.Id,
                    context.Message.Id,
                    aiLoadingMessageId);
            }

            var outcome = await _app.HandleMonzeAsync(
                clanId,
                context.Channel.Id,
                context.Author.Id,
                args,
                context.CancellationToken,
                TryGetSingleMentionedUserId(context),
                await BuildAiRequestAsync(context, args));
            if (isAiCommand)
            {
                _logger.LogInformation(
                    "AI outcome ready. Channel={ChannelId}, SourceMessage={SourceMessageId}, LoadingMessage={LoadingMessageId}, FieldCount={FieldCount}, TextLength={TextLength}.",
                    context.Channel.Id,
                    context.Message.Id,
                    aiLoadingMessageId,
                    outcome.Fields?.Count ?? 0,
                    outcome.Text?.Length ?? 0);
                await UpdateCommandAsync(
                    context,
                    aiLoadingMessageId,
                    MonzeMessageBuilder.Card(outcome, _commandOptions));
                _logger.LogInformation(
                    "AI loading response update sent. Channel={ChannelId}, LoadingMessage={LoadingMessageId}.",
                    context.Channel.Id,
                    aiLoadingMessageId);
            }
            else
            {
                if (outcome.ShowWelcomeSettings && outcome.WelcomeSettings is { } settings)
                {
                    _welcomeSetupDrafts.Set(
                        new WelcomeSetupDraftKey(clanId, context.Channel.Id, context.Author.Id),
                        settings,
                        DateTimeOffset.UtcNow);
                }
                await ReplyCommandAsync(context, MonzeMessageBuilder.Card(outcome, _commandOptions));
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monze command failed for clan {ClanId} and channel {ChannelId}.", clanId, context.Channel.Id);
            var failureResponse = MonzeMessageBuilder.Card(
                MonzeMessages.TitleMonze,
                MonzeMessages.TemporaryFailure,
                MonzeTone.Error);
            if (isAiCommand)
            {
                await UpdateCommandAsync(context, aiLoadingMessageId, failureResponse);
            }
            else
            {
                await context.ReplyAsync(failureResponse);
            }
        }
    }

    internal static bool IsAiCommand(CommandArguments args)
        => args.Length > 1
            && args[0].Equals(MonzeCommandNames.Ai, StringComparison.OrdinalIgnoreCase)
            && MonzeCommandNames.Normalize(args[1]) is MonzeCommandNames.AiSummary
                or MonzeCommandNames.Translate
                or MonzeCommandNames.Composer
                or MonzeCommandNames.Simplify;

    private static bool IsAvatarCommand(string value)
        => value.Equals(MonzeCommandNames.Avatar, StringComparison.OrdinalIgnoreCase)
            || value.Equals(MonzeCommandNames.AvatarAliasAva, StringComparison.OrdinalIgnoreCase)
            || value.Equals(MonzeCommandNames.AvatarAliasAvt, StringComparison.OrdinalIgnoreCase);

    private async Task<AiRequestContext?> BuildAiRequestAsync(
        ICommandContext context,
        CommandArguments args)
    {
        if (args.Length < 2
            || !args[0].Equals(MonzeCommandNames.Ai, StringComparison.OrdinalIgnoreCase)
            || !args[1].Equals(MonzeCommandNames.AiSummary, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A reply summary is intentionally the no-argument form. Ordinary
        // summaries already have their input and should not trigger another
        // history request just to look for an optional reference.
        if (args.Length > 2)
        {
            return null;
        }

        // Some payloads carry the reply id in content metadata. The realtime
        // command context exposes the message entity without its reference
        // list, so fall back to the authoritative message response when the
        // content metadata is absent.
        var replyId = context.Message.Content.ReplyToMessageId;
        if (replyId is not > 0 && context.Clan is not null)
        {
            var lookup = await FetchReplyMessageIdAsync(context);
            replyId = lookup.MessageId;
            _logger.LogDebug(
                "AI reply lookup completed. MessageCount={MessageCount}, ReferenceCount={ReferenceCount}, Found={Found}.",
                lookup.MessageCount,
                lookup.ReferenceCount,
                lookup.MessageId is > 0);
        }
        if (replyId is not long anchorId || anchorId <= 0 || context.Clan is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-1);
        var history = new List<(long Id, long? CreatedAt, string Text)>(32);
        long? anchorCreatedAt = null;
        try
        {
            var response = await context.Client.ListChannelMessagesAsync(
                context.Clan.Id,
                context.Channel.Id,
                anchorId,
                // Direction 1 returns the anchor and newer messages. The
                // summary window is intentionally bounded below by the
                // replied message and above by the command received now.
                direction: 1,
                limit: 100,
                topicId: null,
                options: new RequestOptions { SocketSendTimeout = 5_000 });
            for (var i = 0; i < response.Messages.Count; i++)
            {
                var message = response.Messages[i];
                if (message.MessageId < anchorId || message.MessageId == context.Message.Id)
                {
                    continue;
                }

                var content = MessageContent.Parse(message.Content);
                if (string.IsNullOrWhiteSpace(content.Text))
                {
                    continue;
                }

                var created = message.CreateTimeSeconds > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(message.CreateTimeSeconds)
                    : (DateTimeOffset?)null;
                if (message.MessageId == anchorId && created is { } anchorCreated)
                {
                    anchorCreatedAt = anchorCreated.ToUnixTimeSeconds();
                }

                if (created is { } timestamp && timestamp < cutoff)
                {
                    continue;
                }

                history.Add((message.MessageId, created?.ToUnixTimeSeconds(), content.Text!));
            }
        }
        catch
        {
            // The cached anchor below still makes a reply useful when the
            // history endpoint is temporarily unavailable.
        }

        if (context.Channel.Messages.TryGet(anchorId, out var cached))
        {
            var anchorContent = cached.Content.Text;
            if (!string.IsNullOrWhiteSpace(anchorContent)
                && !history.Any(item => item.Id == anchorId))
            {
                history.Insert(0, (anchorId, anchorCreatedAt, anchorContent));
            }
        }

        var anchorTimestamp = anchorCreatedAt is long createdAtSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(createdAtSeconds)
            : (DateTimeOffset?)null;
        var withinWindow = AiReplyWindow.Contains(
            anchorTimestamp,
            now,
            TimeSpan.FromHours(1));
        if (history.Count == 0)
        {
            return new AiRequestContext(anchorId, null, withinWindow);
        }

        history.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        var builder = new StringBuilder(Math.Min(8_000, history.Count * 120));
        for (var i = 0; i < history.Count; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(history[i].Text);
            if (builder.Length >= 8_000)
            {
                builder.Length = 8_000;
                break;
            }
        }

        return new AiRequestContext(anchorId, builder.ToString(), withinWindow);
    }

    private static async Task<(long? MessageId, int MessageCount, int ReferenceCount)> FetchReplyMessageIdAsync(
        ICommandContext context)
    {
        try
        {
            var response = await context.Client.ListChannelMessagesAsync(
                context.Clan!.Id,
                context.Channel.Id,
                context.Message.Id,
                // Direction 1 asks the API for messages at and after the
                // command id, which includes the command envelope itself.
                direction: 1,
                limit: 100,
                topicId: null,
                options: new RequestOptions { SocketSendTimeout = 5_000 });
            var referenceCount = 0;
            for (var i = 0; i < response.Messages.Count; i++)
            {
                var message = response.Messages[i];
                if (message.MessageId != context.Message.Id)
                {
                    continue;
                }

                referenceCount = message.References.Count;
                for (var j = 0; j < message.References.Count; j++)
                {
                    var reference = message.References[j];
                    if (reference.RefType == 0 && reference.MessageRefId > 0)
                    {
                        return (reference.MessageRefId, response.Messages.Count, referenceCount);
                    }
                }
            }

            return (null, response.Messages.Count, referenceCount);
        }
        catch
        {
            // Reply context is optional. A transient history failure should
            // leave ordinary AI commands usable.
            return (null, -1, -1);
        }
    }

    private string ResolveMonzeCommand(ICommandContext context)
    {
        if (!_commandOptions.HasRoot && !string.IsNullOrWhiteSpace(context.Name))
        {
            return context.Name;
        }

        return context.Args.Count == 0 ? MonzeCommandNames.Help : context.Args[0];
    }

    private static long? TryGetSingleMentionedUserId(ICommandContext context)
    {
        var mentions = context.Message.Mentions;
        if (mentions.Count != 1 || mentions[0].UserId <= 0)
        {
            return null;
        }

        return mentions[0].UserId;
    }

}

