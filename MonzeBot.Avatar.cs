using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Ui;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task HandleAvatarAsync(
        ICommandContext context,
        CommandArguments args)
    {
        var clanId = context.Clan?.Id ?? 0;
        if (clanId <= 0)
        {
            await ReplyCommandAsync(context, MonzeMessageBuilder.Card(
                MonzeMessages.TitleMonze,
                MonzeMessages.UnknownClan,
                MonzeTone.Error));
            return;
        }

        if (!_commandRateLimiter.TryAcquire(
                clanId,
                context.Author.Id,
                MonzeCommandNames.Avatar,
                DateTimeOffset.UtcNow,
                out var retryAfter))
        {
            await ReplyCommandAsync(context, MonzeMessageBuilder.Card(
                MonzeMessages.TitleRateLimited,
                MonzeMessages.RateLimited(retryAfter),
                MonzeTone.Warn));
            return;
        }

        if (args.Length > 0 && args[0].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase))
        {
            var help = await _app.HandleMonzeAsync(
                clanId,
                context.Channel.Id,
                context.Author.Id,
                new CommandArguments(new[] { MonzeCommandNames.Help, MonzeCommandNames.Avatar }),
                context.CancellationToken);
            await ReplyCommandAsync(context, MonzeMessageBuilder.Card(help, _commandOptions));
            return;
        }

        try
        {
            var target = await ResolveAvatarTargetAsync(context, args);
            if (target is null)
            {
                await ReplyCommandAsync(context, MonzeMessageBuilder.Card(
                    MonzeMessages.TitleMonze,
                    MonzeMessages.AvatarNotFound,
                    MonzeTone.Warn));
                return;
            }

            if (string.IsNullOrWhiteSpace(target.AvatarUrl))
            {
                await ReplyCommandAsync(context, MonzeMessageBuilder.Card(
                    MonzeMessages.TitleMonze,
                    MonzeMessages.AvatarUnavailable,
                    MonzeTone.Warn));
                return;
            }

            await ReplyCommandAsync(context, MonzeMessageBuilder.Avatar(target));
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException ex) when (ex.Message == MonzeMessages.AvatarReplyRequired)
        {
            await ReplyCommandAsync(context, MonzeMessageBuilder.Card(
                MonzeMessages.TitleMonze,
                ex.Message,
                MonzeTone.Warn));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Avatar command failed for clan {ClanId}.", clanId);
            await ReplyCommandAsync(context, MonzeMessageBuilder.Card(
                MonzeMessages.TitleMonze,
                MonzeMessages.TemporaryFailure,
                MonzeTone.Error));
        }
    }

    private async Task<MonzeAvatarTarget?> ResolveAvatarTargetAsync(
        ICommandContext context,
        CommandArguments args)
    {
        if (args.Length == 0)
        {
            return await FindClanUserAsync(
                context.Client,
                context.Clan!.Id,
                context.Author.Id,
                null,
                context.CancellationToken);
        }

        if (args.Length == 2
            && args[0].Equals("reply", StringComparison.OrdinalIgnoreCase)
            && args[1].Equals("message", StringComparison.OrdinalIgnoreCase))
        {
            var reply = await FetchReplyMessageIdAsync(context);
            if (reply.MessageId is not long messageId || messageId <= 0)
            {
                throw new InvalidOperationException(MonzeMessages.AvatarReplyRequired);
            }

            var response = await context.Client.ListChannelMessagesAsync(
                context.Clan!.Id,
                context.Channel.Id,
                messageId,
                direction: 1,
                limit: 100,
                topicId: null,
                options: new RequestOptions { SocketSendTimeout = 5_000 });
            for (var i = 0; i < response.Messages.Count; i++)
            {
                var message = response.Messages[i];
                if (message.MessageId != messageId || message.SenderId <= 0)
                {
                    continue;
                }

                await _userProfiles.UpsertAsync(
                    context.Clan!.Id,
                    message.SenderId,
                    message.ClanNick,
                    message.DisplayName,
                    message.Username,
                    message.Avatar,
                    context.CancellationToken);
                return new MonzeAvatarTarget(
                    message.SenderId,
                    FirstUserLabel(message.ClanNick, message.DisplayName, message.Username),
                    message.Avatar);
            }

            return null;
        }

        if (args.Length != 1)
        {
            return null;
        }

        var mentionedUserId = TryGetSingleMentionedUserId(context);
        var username = mentionedUserId is null ? args[0].Trim().TrimStart('@') : null;
        return await FindClanUserAsync(
            context.Client,
            context.Clan!.Id,
            mentionedUserId,
            username,
            context.CancellationToken);
    }

    private async Task<MonzeAvatarTarget?> FindClanUserAsync(
        MezonClient client,
        long clanId,
        long? userId,
        string? username,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = userId is long storedUserId
            ? await _userProfiles.GetByIdAsync(clanId, storedUserId, cancellationToken)
            : await _userProfiles.FindByUsernameAsync(clanId, username!, cancellationToken);

        var members = await client.ListClanUsersAsync(
            clanId,
            new RequestOptions { SocketSendTimeout = 5_000 });
        for (var i = 0; i < members.ClanUsers.Count; i++)
        {
            var member = members.ClanUsers[i];
            var user = member.User;
            if (user.Id <= 0)
            {
                continue;
            }

            var matchesId = userId is long requestedId && requestedId == user.Id;
            var matchesUsername = username is not null
                && user.Username.Equals(username, StringComparison.OrdinalIgnoreCase);
            if (!matchesId && !matchesUsername)
            {
                continue;
            }

            var result = new MonzeAvatarTarget(
                user.Id,
                FirstUserLabel(member.ClanNick, user.DisplayName, user.Username),
                user.AvatarUrl);
            await _userProfiles.UpsertAsync(
                clanId,
                user.Id,
                member.ClanNick,
                user.DisplayName,
                user.Username,
                user.AvatarUrl,
                cancellationToken);
            return result;
        }

        return snapshot is null
            ? null
            : new MonzeAvatarTarget(snapshot.UserId, snapshot.Label, snapshot.AvatarUrl);
    }

    private static string FirstUserLabel(params string?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(values[i]))
            {
                return values[i]!.Trim().TrimStart('@');
            }
        }

        return MonzeMessages.MemberFallbackLabel;
    }
}
