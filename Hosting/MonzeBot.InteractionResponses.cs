using Mezon.Net.Client;
using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Mezon.Net.Sdk.Interactions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Ui;
using SdkMezonClient = Mezon.Net.Sdk.MezonClient;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task DispatchButtonInteractionAsync(
        SdkMezonClient client,
        InteractionRouter router,
        MessageButtonClickedEventData evt)
    {
        var response = (MessageButtonClickedResponse)evt;
        _logger.LogDebug(
            "Button interaction event received. Channel={ChannelId}, Message={MessageId}, User={UserId}, CustomId={CustomId}.",
            response.ChannelId,
            response.MessageId,
            response.UserId,
            response.ButtonId);

        try
        {
            var result = await router.HandleButtonAsync(client, evt).ConfigureAwait(false);
            _logger.LogDebug(
                "Button interaction dispatch completed. Channel={ChannelId}, Message={MessageId}, CustomId={CustomId}, Result={Result}.",
                response.ChannelId,
                response.MessageId,
                response.ButtonId,
                result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Button interaction dispatch failed. Channel={ChannelId}, Message={MessageId}, CustomId={CustomId}.",
                response.ChannelId,
                response.MessageId,
                response.ButtonId);
        }
    }

    private async Task DispatchSelectInteractionAsync(
        SdkMezonClient client,
        InteractionRouter router,
        DropdownBoxSelectedEventData evt)
    {
        var response = (DropdownBoxSelectedResponse)evt;
        try
        {
            var result = await router.HandleSelectAsync(client, evt).ConfigureAwait(false);
            _logger.LogDebug(
                "Select interaction dispatch completed. Channel={ChannelId}, Message={MessageId}, SelectId={SelectId}, Result={Result}.",
                response.ChannelId,
                response.MessageId,
                response.SelectboxId,
                result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Select interaction dispatch failed. Channel={ChannelId}, Message={MessageId}, SelectId={SelectId}.",
                response.ChannelId,
                response.MessageId,
                response.SelectboxId);
        }
    }

    private async Task<long> ReplyCommandAsync(
        ICommandContext context,
        MessageContent content)
    {
        var isInteractive = HasInteractiveComponents(content);
        var response = isInteractive
            ? await context.Channel.SendEphemeralAsync(content, context.Author.Id)
            : await context.ReplyAsync(content);
        var messageId = TryReadMessageId(response);
        if (isInteractive)
        {
            _logger.LogDebug(
                "Private interaction response tracked. Clan={ClanId}, Channel={ChannelId}, Message={MessageId}, User={UserId}.",
                context.Channel.ClanId,
                context.Channel.Id,
                messageId,
                context.Author.Id);
            RememberPrivateInteraction(
                context.Channel.ClanId,
                context.Channel.Id,
                messageId,
                context.Author.Id);
        }

        return messageId;
    }

    private async Task<long> UpdateCommandAsync(
        ICommandContext context,
        long messageId,
        MessageContent content)
    {
        if (messageId <= 0)
        {
            return await ReplyCommandAsync(context, content);
        }

        await context.Channel.UpdateMessageAsync(messageId, content);
        return messageId;
    }

    private async Task<long> UpdatePrivateInteractionAsync(
        IInteractionContext context,
        MessageContent content)
    {
        var messageId = context.Interaction.MessageId;
        if (messageId <= 0)
        {
            _logger.LogWarning(
                "Cannot update ephemeral interaction because the source message is unavailable. Clan={ClanId}, Channel={ChannelId}.",
                context.Channel.ClanId,
                context.Channel.Id);
            return 0;
        }

        var response = await context.UpdateEphemeralAsync(messageId, content);
        var updatedMessageId = TryReadMessageId(response);
        if (updatedMessageId <= 0)
        {
            updatedMessageId = messageId;
        }

        RememberPrivateInteraction(
            context.Channel.ClanId,
            context.Channel.Id,
            updatedMessageId,
            context.User.Id);
        return updatedMessageId;
    }

    private async Task DeletePrivateInteractionAsync(IInteractionContext context)
    {
        var messageId = context.Interaction.MessageId;
        if (messageId <= 0)
        {
            _logger.LogWarning(
                "Cannot delete ephemeral interaction because the source message is unavailable. Clan={ClanId}, Channel={ChannelId}.",
                context.Channel.ClanId,
                context.Channel.Id);
            return;
        }

        await context.DeleteEphemeralAsync(messageId);
        _policyCache.Remove(PrivateInteractionKey(
            context.Channel.ClanId,
            context.Channel.Id,
            messageId));
    }

    private Task<bool> EnsurePrivateInteractionAsync(IInteractionContext context)
    {
        var serverAuthenticated = context.Interaction is IInteractionActor actor
            && actor.ActorTrust == InteractionActorTrust.ServerAuthenticated;
        var messageId = context.Interaction.MessageId;
        var ownerId = 0L;
        var hasBinding = messageId > 0
            && _policyCache.TryGetValue(
                PrivateInteractionKey(context.Channel.ClanId, context.Channel.Id, messageId),
                out ownerId);
        if (!hasBinding)
        {
            hasBinding = _policyCache.TryGetValue(
                PrivateInteractionUserKey(context.Channel.ClanId, context.Channel.Id, context.User.Id),
                out ownerId);
        }

        var canHandle = hasBinding
            && ownerId > 0
            && ownerId == context.User.Id
            && serverAuthenticated;
        if (canHandle)
        {
            return Task.FromResult(true);
        }

        if (!serverAuthenticated)
        {
            _logger.LogWarning(
                "Rejected interaction with client-supplied actor. Clan={ClanId}, Channel={ChannelId}, Message={MessageId}.",
                context.Channel.ClanId,
                context.Channel.Id,
                messageId);
        }
        else
        {
            _logger.LogDebug(
                "Private interaction rejected by binding. Clan={ClanId}, Channel={ChannelId}, Message={MessageId}, User={UserId}.",
                context.Channel.ClanId,
                context.Channel.Id,
                messageId,
                context.User.Id);
        }

        return Task.FromResult(false);
    }

    private static long TryReadMessageId(ChannelMessageAckResponse response)
    {
        if (response.Equals(default(ChannelMessageAckResponse)))
        {
            return 0;
        }

        try
        {
            return response.MessageId;
        }
        catch (NullReferenceException)
        {
            // Keep the guard for a malformed non-default wrapper returned by
            // an older SDK build.
            return 0;
        }
    }

    private void RememberPrivateInteraction(long clanId, long channelId, long messageId, long userId)
    {
        if (userId <= 0)
        {
            return;
        }

        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
            Size = 1
        };
        _policyCache.Set(PrivateInteractionUserKey(clanId, channelId, userId), userId, cacheOptions);
        if (messageId > 0)
        {
            _policyCache.Set(PrivateInteractionKey(clanId, channelId, messageId), userId, cacheOptions);
        }
    }

    private static string PrivateInteractionKey(long clanId, long channelId, long messageId)
        => $"monze:private-interaction:{clanId}:{channelId}:{messageId}";

    private static string PrivateInteractionUserKey(long clanId, long channelId, long userId)
        => $"monze:private-interaction-user:{clanId}:{channelId}:{userId}";

    private static bool HasInteractiveComponents(MessageContent content)
    {
        if (content.Components is { Count: > 0 } rows)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var components = rows[i].Components;
                for (var j = 0; j < components.Count; j++)
                {
                    if (IsInteractive(components[j]))
                    {
                        return true;
                    }
                }
            }
        }

        if (content.Embeds is not { Count: > 0 } embeds)
        {
            return false;
        }

        for (var i = 0; i < embeds.Count; i++)
        {
            var fields = embeds[i].Fields;
            if (fields is null)
            {
                continue;
            }

            for (var j = 0; j < fields.Count; j++)
            {
                var field = fields[j];
                if (field.Input is not null && IsInteractive(field.Input))
                {
                    return true;
                }

                if (field.Buttons is not { Count: > 0 } buttons)
                {
                    continue;
                }

                for (var k = 0; k < buttons.Count; k++)
                {
                    if (IsInteractive(buttons[k]))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsInteractive(MessageComponent component)
        => component.ComponentType is MessageComponentType.Button
            or MessageComponentType.Select
            or MessageComponentType.Input
            or MessageComponentType.DatePicker
            or MessageComponentType.Radio;

}
